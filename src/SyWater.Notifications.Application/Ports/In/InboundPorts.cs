using SyWater.Notifications.Application.Views;

namespace SyWater.Notifications.Application.Ports.In;

/// <summary>My inbox: personal notifications + the ones sent to my roles.</summary>
public interface IListMyNotificationsUseCase
{
    Task<IReadOnlyList<NotificationView>> ExecuteAsync(Requester requester, bool unreadOnly, DateTime? beforeUtc, int? limit, CancellationToken ct);
}

/// <summary>The number on the bell icon.</summary>
public interface IGetUnreadCountUseCase
{
    Task<UnreadCountView> ExecuteAsync(Requester requester, CancellationToken ct);
}

public interface IMarkNotificationReadUseCase
{
    Task<NotificationView> ExecuteAsync(Requester requester, Guid notificationId, CancellationToken ct);
}

public interface IMarkAllNotificationsReadUseCase
{
    /// <returns>How many notifications were marked.</returns>
    Task<int> ExecuteAsync(Requester requester, CancellationToken ct);
}

/// <summary>HU-027: sends the mails of the outbox that are due. Returns how many left.</summary>
public interface ISendDueEmailsUseCase
{
    Task<int> ExecuteAsync(CancellationToken ct);
}

/// <summary>HU-028: sends the SMS / WhatsApp messages of the outbox that are due. Returns how many left.</summary>
public interface ISendDueShortMessagesUseCase
{
    Task<int> ExecuteAsync(CancellationToken ct);
}

/// <summary>
/// HU-028: ms-iam asks to deliver the code that verifies a phone, by the mode the user picked. Sent at once (the user is
/// waiting for it) and never stored here.
/// </summary>
public interface ISendVerificationCodeUseCase
{
    /// <returns>The id the provider gave to the message.</returns>
    Task<string?> ExecuteAsync(string toPhone, string? channel, string code, int validMinutes, CancellationToken ct);
}

/// <summary>HU-032: tells the owners whose device went silent. Returns how many alerts were created.</summary>
public interface IDetectOfflineDevicesUseCase
{
    Task<int> ExecuteAsync(CancellationToken ct);
}
