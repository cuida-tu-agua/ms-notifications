using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Infrastructure.ShortMessages;

/// <summary>Section "Meta". The AccessToken goes in user-secrets / infra .env, never in git.</summary>
public sealed class MetaWhatsAppOptions
{
    public const string Section = "Meta";

    /// <summary>Token of the app (temporary one of "API Setup" for a demo, a system user token for longer).</summary>
    public string AccessToken { get; init; } = "";

    /// <summary>"Phone number ID" of API Setup (the test number of Meta, or the business number).</summary>
    public string PhoneNumberId { get; init; } = "";

    /// <summary>
    /// Optional approved template whose body has ONE variable {{1}} that receives the whole text (category Utility), e.g.
    /// "Cuida Tu Agua: {{1}}". With it the messages arrive at any time; without it they are free text, which WhatsApp only
    /// delivers within 24 h after the person wrote to the business number.
    /// </summary>
    public string TemplateName { get; init; } = "";

    /// <summary>Optional approved Authentication template (code + "copy code" button) for the phone verification code.</summary>
    public string CodeTemplateName { get; init; } = "";

    public string TemplateLanguage { get; init; } = "es";
    public string ApiVersion { get; init; } = "v23.0";
    public string BaseUrl { get; init; } = "https://graph.facebook.com";
    public int TimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// HU-028: WhatsApp Cloud API of Meta (POST /{version}/{phone-number-id}/messages, Bearer token). Free text, the general
/// template (one variable) or the authentication template for codes, depending on what is configured.
/// Answers: 429 / 5xx / an expired token / Meta "try later" codes = try again later; any other 4xx = Meta refused this
/// number or message for good (not in the test allow list, no WhatsApp, outside the 24 h window, template not approved...).
/// </summary>
public sealed partial class MetaWhatsAppSender(HttpClient http, IOptions<MetaWhatsAppOptions> options) : IShortMessageSender
{
    /// <summary>Meta error codes that mean "not now": rate limits, temporary errors, the token (ours, not the user's).</summary>
    private static readonly HashSet<int> RetryCodes = [1, 2, 4, 190, 80007, 130429, 131000, 131016, 131048, 131056, 133004];

    private readonly MetaWhatsAppOptions _options = options.Value;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public async Task<string?> SendAsync(ShortMessage message, CancellationToken ct)
    {
        if (message.Channel != ShortMessageChannel.WhatsApp)
            throw new ShortMessageRejectedException("The Meta adapter only sends WhatsApp messages.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiVersion.Trim('/')}/{_options.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(Payload(message)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ShortMessageSendException("Meta could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ShortMessageSendException("Meta did not answer in time.", ex);
        }

        using (response)
        {
            var json = await ReadJson(response, ct);
            if (response.IsSuccessStatusCode)
                return json is { } ok && ok.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array
                       && list.GetArrayLength() > 0 && list[0].TryGetProperty("id", out var id)
                    ? id.GetString()
                    : null;

            var error = json is { } body && body.TryGetProperty("error", out var e) ? e : (JsonElement?)null;
            var code = error is { } err && err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : (int?)null;
            var text = error is { } er && er.TryGetProperty("message", out var m) ? m.GetString() : null;
            var details = error is { } ed && ed.TryGetProperty("error_data", out var data) && data.ValueKind == JsonValueKind.Object
                          && data.TryGetProperty("details", out var d) ? d.GetString() : null;
            var reason = $"Meta {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}: {text ?? response.ReasonPhrase}" +
                         (string.IsNullOrWhiteSpace(details) ? "" : $" - {details}");

            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized
                || (code is { } known && RetryCodes.Contains(known)))
                throw new ShortMessageSendException(reason);
            throw new ShortMessageRejectedException(reason, code?.ToString());
        }
    }

    /// <summary>The JSON body for this message (public for the tests).</summary>
    public object Payload(ShortMessage message)
    {
        var to = message.ToPhone.TrimStart('+');

        if (message.Kind == ShortMessageKind.VerificationCode && !string.IsNullOrWhiteSpace(message.Code)
            && !string.IsNullOrWhiteSpace(_options.CodeTemplateName))
            return new
            {
                messaging_product = "whatsapp",
                to,
                type = "template",
                template = new
                {
                    name = _options.CodeTemplateName,
                    language = new { code = _options.TemplateLanguage },
                    components = new object[]
                    {
                        new { type = "body", parameters = new[] { new { type = "text", text = message.Code } } },
                        new { type = "button", sub_type = "url", index = "0", parameters = new[] { new { type = "text", text = message.Code } } },
                    },
                },
            };

        if (!string.IsNullOrWhiteSpace(_options.TemplateName))
            return new
            {
                messaging_product = "whatsapp",
                to,
                type = "template",
                template = new
                {
                    name = _options.TemplateName,
                    language = new { code = _options.TemplateLanguage },
                    components = new object[]
                    {
                        // A template variable cannot have line breaks, tabs or more than 4 spaces in a row
                        new { type = "body", parameters = new[] { new { type = "text", text = Spaces().Replace(message.Body, " ").Trim() } } },
                    },
                },
            };

        return new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to,
            type = "text",
            text = new { preview_url = false, body = message.Body },
        };
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

/// <summary>
/// Each mode by its own provider: WhatsApp by Meta and SMS by Twilio / Mailpit, for example. A mode that has no provider
/// refuses the message for good (FAILED, and a verification code answers 422 so the user picks the other mode).
/// </summary>
public sealed class ChannelRoutingShortMessageSender(IShortMessageSender? sms, IShortMessageSender? whatsApp) : IShortMessageSender
{
    public Task<string?> SendAsync(ShortMessage message, CancellationToken ct)
    {
        var sender = message.Channel == ShortMessageChannel.WhatsApp ? whatsApp : sms;
        return sender is null
            ? throw new ShortMessageRejectedException($"{message.Channel.ToCode()} is not configured in this environment.")
            : sender.SendAsync(message, ct);
    }
}
