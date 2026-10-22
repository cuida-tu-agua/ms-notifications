using System.Net;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Infrastructure.ShortMessages;

/// <summary>
/// DEVELOPMENT ONLY (Sms:Provider = "mailpit"): every SMS / WhatsApp becomes a mail in Mailpit (http://localhost:8025),
/// so the team sees the alerts and the verification codes without a Twilio account or a real phone. The mail goes to
/// "sms.573001112233@sms.cuidatuagua.local" (or "whatsapp.…"), so the inbox can be filtered by number or by mode.
/// The text is the one the phone would show: without accents for SMS, as it is for WhatsApp.
/// </summary>
public sealed class MailpitShortMessageSender(IEmailSender email) : IShortMessageSender
{
    public const string Domain = "sms.cuidatuagua.local";

    public async Task<string?> SendAsync(ShortMessage message, CancellationToken ct)
    {
        var mode = message.Channel == ShortMessageChannel.WhatsApp ? "WhatsApp" : "SMS";
        var body = message.Channel == ShortMessageChannel.WhatsApp ? message.Body : GsmText.ForSms(message.Body);
        var address = $"{mode.ToLowerInvariant()}.{message.ToPhone.TrimStart('+')}@{Domain}";
        var id = $"mailpit-{Guid.NewGuid():N}";

        try
        {
            await email.SendAsync(new EmailMessage(
                address,
                message.ToPhone,
                $"[{mode}] {message.ToPhone}",
                $"{body}\n\n({mode} a {message.ToPhone} · {body.Length} caracteres · id {id})",
                $"<pre style=\"font-family:Arial,sans-serif;white-space:pre-wrap\">{WebUtility.HtmlEncode(body)}</pre>" +
                $"<p style=\"color:#666;font-size:12px\">{mode} a {WebUtility.HtmlEncode(message.ToPhone)} · {body.Length} caracteres · id {id}</p>"), ct);
        }
        catch (EmailSendException ex)
        {
            throw new ShortMessageSendException("Mailpit (fake SMS) could not be reached.", ex);
        }

        return id;
    }
}
