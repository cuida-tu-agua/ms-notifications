using SyWater.Notifications.Domain.Messages;

namespace SyWater.Notifications.Application.Views;

/// <param name="Key">ALERT_CRITICAL, PHONE_CODE, COLLABORATOR_INVITATION or GUEST_LINK_SHARE.</param>
/// <param name="Body">The text in use right now.</param>
/// <param name="IsCustom">False = the default text.</param>
public sealed record MessageTemplateView(
    string Key, string Body, string DefaultBody, bool IsCustom, IReadOnlyList<MessagePlaceholderView> Placeholders,
    int MaxLength, DateTime? UpdatedAt)
{
    public static MessageTemplateView From(MessageTemplate t) => new(
        t.Key.ToCode(), t.Body, t.Definition.DefaultBody, t.IsCustom,
        t.Definition.Placeholders.Select(p => new MessagePlaceholderView(p.Name, p.Token, p.Required)).ToList(),
        MessageTemplate.MaxBodyLength, t.UpdatedAt);
}

/// <param name="Token">What goes in the text, e.g. "{place}".</param>
public sealed record MessagePlaceholderView(string Name, string Token, bool Required);

/// <param name="InApp">A notification was stored for the person (false: no account, or already notified).</param>
/// <param name="ShortMessageQueued">A WhatsApp / SMS is on its way (the phone is checked when it is sent).</param>
public sealed record InvitationNoticeView(bool InApp, bool ShortMessageQueued);

/// <param name="Sent">The provider took the message.</param>
/// <param name="Problem">sms_disabled · no_verified_phone · rejected (the provider refused it for good) · unavailable (try later).</param>
/// <param name="Detail">The reason the provider gave (e.g. "Meta 400 (131030): Recipient phone number not in allowed list").</param>
/// <param name="Body">The text that was sent.</param>
/// <param name="DeliveryStatus">What the provider said a few seconds later (Twilio: queued, sent, delivered, read...); null if it cannot tell.</param>
public sealed record TestMessageView(
    bool Sent, string? Channel, string? MaskedPhone, string? ProviderMessageId, string? Problem, string? Detail, string? Body,
    string? DeliveryStatus = null)
{
    public static TestMessageView Failed(string problem, string? detail) => new(false, null, null, null, problem, detail, null);
}

/// <param name="Sms">twilio · mailpit · "" (off).</param>
/// <param name="WhatsApp">meta · twilio · mailpit · "" (off).</param>
public sealed record ShortMessageProvidersView(string Sms, string WhatsApp, bool WhatsAppTemplate);

/// <param name="Channels">WHATSAPP and / or SMS, in the order the app should offer them.</param>
public sealed record ShortMessageChannelsView(IReadOnlyList<string> Channels);
