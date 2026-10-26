using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.Views;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// HU-042: the invited person, if they already have an account, learns it in the notification center (level Important:
/// app + push by default) and by WhatsApp / SMS when their phone is verified and they keep that channel on (the same switch
/// as the critical alerts). The e-mail is not repeated: ms-iam already mailed the invitation. Idempotent per invitation.
/// </summary>
public sealed class NotifyCollaboratorInvitationUseCase(
    NotificationDispatcher dispatcher,
    INotificationPreferenceRepository preferences,
    TimeProvider clock,
    ISmsOutboxRepository? smsOutbox = null,
    SmsSettings? sms = null,
    MessageTexts? texts = null) : INotifyCollaboratorInvitationUseCase
{
    public async Task<InvitationNoticeView> ExecuteAsync(CollaboratorInvitationNotice notice, CancellationToken ct)
    {
        if (notice.InvitationId == Guid.Empty || notice.PlaceId == Guid.Empty)
            throw new InvalidNotificationException("The invitation and the place are required.");
        if (notice.InviteeUserId is not { } invitee || invitee == Guid.Empty)
            return new InvitationNoticeView(false, false);   // no account yet: the e-mail of ms-iam is the only way to reach them

        var place = InvitationTexts.Clean(notice.PlaceName, "un lugar");
        var inviter = InvitationTexts.Clean(notice.InviterName, "Alguien");
        var now = clock.GetUtcNow().UtcDateTime;
        var eventId = $"invitation:{notice.InvitationId:N}";

        var notification = Notification.Create(Audience.ForUser(invitee), NotificationType.CollaboratorInvitation,
            NotificationSeverity.Warning, "Te invitaron a colaborar",
            $"{inviter} te invitó a colaborar en \"{place}\". Acéptala o recházala en Invitaciones.",
            notice.PlaceId, eventId, now);
        await dispatcher.DispatchAsync(notification, new DeliveryContext(place), ct, new DispatchOptions(SkipEmail: true));

        var wanted = await preferences.GetAsync(invitee, ct);
        var queued = false;
        if (sms is { Enabled: true } && smsOutbox is not null
            && wanted.ChannelsFor(NotificationSeverity.Critical).HasFlag(NotificationChannels.Sms))
        {
            var template = await (texts ?? MessageTexts.Defaults).GetAsync(MessageTemplateKey.CollaboratorInvitation, ct);
            queued = await smsOutbox.AddAsync(SmsOutboxItem.Queue(invitee, eventId,
                ShortMessageComposer.ForInvitation(place, inviter, notice.Permissions, notice.ExpiresAt, template), now), ct);
        }

        return new InvitationNoticeView(wanted.ChannelsFor(NotificationSeverity.Warning).HasFlag(NotificationChannels.InApp), queued);
    }
}

/// <summary>HU-043: the owner learns in the app that the invited person accepted (and now collaborates) or rejected.</summary>
public sealed class NotifyInvitationAnsweredUseCase(NotificationDispatcher dispatcher, TimeProvider clock) : INotifyInvitationAnsweredUseCase
{
    public async Task<InvitationNoticeView> ExecuteAsync(InvitationAnsweredNotice notice, CancellationToken ct)
    {
        if (notice.InvitationId == Guid.Empty || notice.PlaceId == Guid.Empty || notice.OwnerId == Guid.Empty)
            throw new InvalidNotificationException("The invitation, the place and the owner are required.");

        var place = InvitationTexts.Clean(notice.PlaceName, "tu lugar");
        var who = InvitationTexts.Clean(notice.InviteeName, "La persona invitada");
        var notification = Notification.Create(Audience.ForUser(notice.OwnerId), NotificationType.InvitationAnswered,
            NotificationSeverity.Info,
            notice.Accepted ? "Invitación aceptada" : "Invitación rechazada",
            notice.Accepted
                ? $"{who} aceptó tu invitación y ya colabora en \"{place}\"."
                : $"{who} rechazó tu invitación a colaborar en \"{place}\".",
            notice.PlaceId, $"invitation-answer:{notice.InvitationId:N}", clock.GetUtcNow().UtcDateTime);

        await dispatcher.DispatchAsync(notification, new DeliveryContext(place), ct);
        return new InvitationNoticeView(true, false);
    }
}

internal static class InvitationTexts
{
    /// <summary>Names come from other users: one line, and short enough for the title and the WhatsApp.</summary>
    public static string Clean(string? text, string fallback)
    {
        var line = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0) return fallback;
        return line.Length <= 80 ? line : line[..80].TrimEnd() + "...";
    }
}
