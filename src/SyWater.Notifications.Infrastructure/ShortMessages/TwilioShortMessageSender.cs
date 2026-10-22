using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    /// <summary>Twilio phone number that sends SMS, E.164 ("+15005550006"). Required for SMS.</summary>
    public string SmsFrom { get; init; } = "";
    /// <summary>WhatsApp sender, E.164 (the sandbox one in development: "+14155238886"). Required for WhatsApp.</summary>
    public string WhatsAppFrom { get; init; } = "";
    public string BaseUrl { get; init; } = "https://api.twilio.com";
    public int TimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// HU-028: Twilio Programmable Messaging, one REST call for SMS and WhatsApp
/// (POST /2010-04-01/Accounts/{AccountSid}/Messages.json, form To / From / Body, Basic auth AccountSid:AuthToken).
/// WhatsApp only changes the addresses ("whatsapp:+57..."). The SMS text drops the accents that would make it 2-3 SMS.
/// 4xx (except 429) = Twilio refused this number / mode for good; 429, 5xx or no answer = try again later.
/// </summary>
public sealed class TwilioShortMessageSender(HttpClient http, IOptions<TwilioOptions> options) : IShortMessageSender
{
    private readonly TwilioOptions _options = options.Value;

    public async Task<string?> SendAsync(ShortMessage message, CancellationToken ct)
    {
        var whatsApp = message.Channel == ShortMessageChannel.WhatsApp;
        var from = whatsApp ? _options.WhatsAppFrom : _options.SmsFrom;
        if (string.IsNullOrWhiteSpace(from))
            throw new ShortMessageRejectedException($"No Twilio sender is configured for {message.Channel.ToCode()}.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"2010-04-01/Accounts/{_options.AccountSid}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = whatsApp ? $"whatsapp:{message.ToPhone}" : message.ToPhone,
                ["From"] = whatsApp ? $"whatsapp:{from}" : from,
                ["Body"] = whatsApp ? message.Body : GsmText.ForSms(message.Body),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ShortMessageSendException("Twilio could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ShortMessageSendException("Twilio did not answer in time.", ex);
        }

        using (response)
        {
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
