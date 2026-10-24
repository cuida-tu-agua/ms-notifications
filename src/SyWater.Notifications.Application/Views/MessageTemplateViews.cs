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
