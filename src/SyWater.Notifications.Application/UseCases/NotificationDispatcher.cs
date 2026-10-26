using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Emails;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// Where a freshly created notification goes, according to the channels its owner chose for its urgency
/// level (HU-034). In-app = the notification center; e-mail = queued in the outbox (HU-027); push = Expo, sent now
/// to every phone of the user (HU-026); SMS / WhatsApp = queued in the sms outbox, CRITICAL only (HU-028).
/// Every step is idempotent, so a redelivered event can safely run the whole thing again.
/// </summary>
public sealed class NotificationDispatcher(
    INotificationRepository notifications,
    INotificationPreferenceRepository preferences,
    IEmailOutboxRepository outbox,
    EmailSettings email,
    PushNotifier push,
    TimeProvider clock,
    ISmsOutboxRepository? smsOutbox = null,
    SmsSettings? sms = null,
    MessageTexts? texts = null)
{
    /// <summary>
    /// Stores it in-app if the user keeps that channel (CRITICAL always does) and queues the mail if they want
    /// it. A broadcast to a role is not personal: it is always stored in-app and never mailed.
    /// </summary>
    /// <returns>The channels the user wants that this service did not deliver (SMS while it is switched off, push likewise).</returns>
    public async Task<NotificationChannels> DispatchAsync(Notification notification, DeliveryContext context, CancellationToken ct,
        DispatchOptions? options = null)
    {
        if (notification.Audience.UserId is not { } userId)
        {
            await notifications.AddAsync(notification, ct);
            return NotificationChannels.None;
        }

        var channels = (await preferences.GetAsync(userId, ct)).ChannelsFor(notification.Severity);
        var isNew = true;

        if (channels.HasFlag(NotificationChannels.InApp))
            isNew = await notifications.AddAsync(notification, ct);

        if (channels.HasFlag(NotificationChannels.Email) && email.Enabled && options is not { SkipEmail: true })
        {
            var (subject, text, html) = EmailComposer.Compose(notification, context, email.AppBaseUrl);
            await outbox.AddAsync(
                EmailOutboxItem.Queue(userId, notification.SourceEventId, subject, text, html, clock.GetUtcNow().UtcDateTime), ct);
        }

        // A redelivered event (already stored in-app) must not buzz the phone twice
        if (channels.HasFlag(NotificationChannels.Push) && isNew)
            await push.NotifyAsync(userId, notification, ct);

        // HU-028: only CRITICAL alerts go by SMS / WhatsApp; the outbox is idempotent per (source event, user)
        var smsQueued = false;
        if (channels.HasFlag(NotificationChannels.Sms) && notification.Severity == NotificationSeverity.Critical
            && sms is { Enabled: true } && smsOutbox is not null)
        {
            var template = await (texts ?? MessageTexts.Defaults).GetAsync(MessageTemplateKey.AlertCritical, ct);
            await smsOutbox.AddAsync(
                SmsOutboxItem.Queue(userId, notification.SourceEventId, ShortMessageComposer.ForAlert(notification, context, template),
                    clock.GetUtcNow().UtcDateTime), ct);
            smsQueued = true;
        }

        var notDelivered = (smsQueued ? NotificationChannels.None : NotificationChannels.Sms)
                           | (push.Enabled ? NotificationChannels.None : NotificationChannels.Push);
        return channels & notDelivered;
    }
}

/// <summary>How a dispatch differs from the usual one. SkipEmail: the person already got a mail about it (an invitation is mailed by ms-iam).</summary>
public sealed record DispatchOptions(bool SkipEmail = false);
