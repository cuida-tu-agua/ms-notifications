using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Infrastructure.ShortMessages;

/// <summary>Section "Twilio". The AuthToken goes in user-secrets / infra .env, never in git.</summary>
public sealed class TwilioOptions
{
    public const string Section = "Twilio";

    public string AccountSid { get; init; } = "";
    public string AuthToken { get; init; } = "";
    /// <summary>Twilio phone number that sends SMS, E.164 ("+15005550006"). Empty = no SMS (WhatsApp only).</summary>
    public string SmsFrom { get; init; } = "";
    /// <summary>WhatsApp sender registered in Twilio, E.164 ("+1…"). Empty = no WhatsApp.</summary>
    public string WhatsAppFrom { get; init; } = "";

    /// <summary>
    /// Content SID ("HX…") of an approved WhatsApp template whose body has ONE variable {{1}} that receives the whole text.
    /// With it the messages arrive at any time; without it they are free text, which WhatsApp only delivers within 24 h
    /// after the person wrote to the number.
    /// </summary>
    public string WhatsAppTemplateSid { get; init; } = "";

    /// <summary>Content SID of an approved WhatsApp authentication template ({{1}} = the code) for the phone code.</summary>
    public string CodeTemplateSid { get; init; } = "";

    public string BaseUrl { get; init; } = "https://api.twilio.com";
    public int TimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// HU-028: Twilio Programmable Messaging, one REST call for SMS and WhatsApp
/// (POST /2010-04-01/Accounts/{AccountSid}/Messages.json, form To / From / Body, Basic auth AccountSid:AuthToken).
/// WhatsApp changes the addresses ("whatsapp:+57...") and, with an approved template, sends ContentSid + ContentVariables
/// instead of Body. The SMS text drops the accents that would make it 2-3 SMS.
/// 4xx (except 429) = Twilio refused this number / mode for good; 429, 5xx or no answer = try again later.
/// Twilio accepts a message first and reports a WhatsApp failure later: <see cref="ReadAsync"/> asks for it.
/// </summary>
public sealed partial class TwilioShortMessageSender(HttpClient http, IOptions<TwilioOptions> options)
    : IShortMessageSender, IShortMessageStatusReader
{
    private readonly TwilioOptions _options = options.Value;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public async Task<string?> SendAsync(ShortMessage message, CancellationToken ct)
    {
        var whatsApp = message.Channel == ShortMessageChannel.WhatsApp;
        var from = whatsApp ? _options.WhatsAppFrom : _options.SmsFrom;
        if (string.IsNullOrWhiteSpace(from))
            throw new ShortMessageRejectedException($"No Twilio sender is configured for {message.Channel.ToCode()}.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"2010-04-01/Accounts/{_options.AccountSid}/Messages.json")
        {
            Content = new FormUrlEncodedContent(Form(message, from)),
        };
        Authorize(request);

        using var response = await SendRequestAsync(request, ct);
        var json = await ReadJson(response, ct);
        if (response.IsSuccessStatusCode)
            return json is { } ok && ok.TryGetProperty("sid", out var sid) ? sid.GetString() : null;

        var code = json is { } err && err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32().ToString() : null;
        var detail = json is { } e2 && e2.TryGetProperty("message", out var m) ? m.GetString() : null;
        var reason = $"Twilio {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}: {detail ?? response.ReasonPhrase}";

        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ShortMessageSendException(reason);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ShortMessageSendException(reason);   // our credentials, not the user's number: retry after fixing them
        throw new ShortMessageRejectedException(reason, code);
    }

    /// <summary>The form fields for this message (public for the tests).</summary>
    public Dictionary<string, string> Form(ShortMessage message, string from)
    {
        if (message.Channel != ShortMessageChannel.WhatsApp)
            return new() { ["To"] = message.ToPhone, ["From"] = from, ["Body"] = GsmText.ForSms(message.Body) };

        var form = new Dictionary<string, string> { ["To"] = $"whatsapp:{message.ToPhone}", ["From"] = $"whatsapp:{from}" };
        if (message.Kind == ShortMessageKind.VerificationCode && !string.IsNullOrWhiteSpace(message.Code)
            && !string.IsNullOrWhiteSpace(_options.CodeTemplateSid))
        {
            form["ContentSid"] = _options.CodeTemplateSid;
            form["ContentVariables"] = JsonSerializer.Serialize(new Dictionary<string, string> { ["1"] = message.Code });
        }
        else if (!string.IsNullOrWhiteSpace(_options.WhatsAppTemplateSid))
        {
            // A template variable cannot have line breaks, tabs or more than 4 spaces in a row
            form["ContentSid"] = _options.WhatsAppTemplateSid;
            form["ContentVariables"] = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["1"] = Spaces().Replace(message.Body, " ").Trim() });
        }
        else
        {
            form["Body"] = message.Body;
        }
        return form;
    }

    /// <summary>
    /// GET /Messages/{sid}.json: queued, sent, delivered, read, or failed / undelivered with Twilio's error code (63016 =
    /// free text outside the 24 h window, 63003 = the number has no WhatsApp...). Null for an id that is not Twilio's.
    /// </summary>
    public async Task<ShortMessageStatus?> ReadAsync(string providerMessageId, CancellationToken ct)
    {
        if (!(providerMessageId.StartsWith("SM", StringComparison.Ordinal) || providerMessageId.StartsWith("MM", StringComparison.Ordinal)))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"2010-04-01/Accounts/{_options.AccountSid}/Messages/{Uri.EscapeDataString(providerMessageId)}.json");
        Authorize(request);
        using var response = await SendRequestAsync(request, ct);
        if (!response.IsSuccessStatusCode || await ReadJson(response, ct) is not { } json) return null;

        var status = json.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        var code = json.TryGetProperty("error_code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32().ToString() : null;
        var text = json.TryGetProperty("error_message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        return ShortMessageStatus.From(status, code is null ? null : $"Twilio {code}{(text is null ? "" : $": {text}")}");
    }

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));

    private async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ShortMessageSendException("Twilio could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ShortMessageSendException("Twilio did not answer in time.", ex);
        }
    }

    private static async Task<JsonElement?> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
