using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Infrastructure.Persistence.Entities;

namespace SyWater.Notifications.Infrastructure.Persistence;

/// <summary>Domain ⇄ table rows. Enums are stored as the SAME strings the CHECK constraints allow.</summary>
internal static class NotificationMapper
{
    public static Notification ToDomain(NotificationEntity e) => Notification.Restore(
        e.Id, Audience.Restore(e.UserId, e.Role), TypeFromDb(e.Type), SeverityFromDb(e.Severity), e.Title, e.Body,
        e.PlaceId, e.SourceEventId, Utc(e.CreatedAt));

    public static NotificationEntity ToEntity(Notification n) => new()
    {
        Id = n.Id,
        UserId = n.Audience.UserId,
        Role = n.Audience.Role,
        Type = ToDb(n.Type),
        Severity = ToDb(n.Severity),
        Title = n.Title,
        Body = n.Body,
        PlaceId = n.PlaceId,
        SourceEventId = n.SourceEventId,
        CreatedAt = n.CreatedAt,
    };

    public static string ToDb(NotificationType t) => t switch
    {
        NotificationType.DeviceLinked => "DEVICE_LINKED",
        NotificationType.DeviceUnlinked => "DEVICE_UNLINKED",
        NotificationType.ValveChanged => "VALVE_CHANGED",
        NotificationType.DeviceOffline => "DEVICE_OFFLINE",
        NotificationType.CollaboratorInvitation => "COLLABORATOR_INVITATION",
        NotificationType.InvitationAnswered => "INVITATION_ANSWERED",
        _ => "SYSTEM_ANNOUNCEMENT",
    };

    public static string ToDb(NotificationSeverity s) => s switch
    {
        NotificationSeverity.Warning => "WARNING",
        NotificationSeverity.Critical => "CRITICAL",
        _ => "INFO",
    };

    private static NotificationType TypeFromDb(string s) => s switch
    {
        "DEVICE_LINKED" => NotificationType.DeviceLinked,
        "DEVICE_UNLINKED" => NotificationType.DeviceUnlinked,
        "VALVE_CHANGED" => NotificationType.ValveChanged,
        "DEVICE_OFFLINE" => NotificationType.DeviceOffline,
        "COLLABORATOR_INVITATION" => NotificationType.CollaboratorInvitation,
        "INVITATION_ANSWERED" => NotificationType.InvitationAnswered,
        _ => NotificationType.SystemAnnouncement,
    };

    private static NotificationSeverity SeverityFromDb(string s) => s switch
    {
        "WARNING" => NotificationSeverity.Warning,
        "CRITICAL" => NotificationSeverity.Critical,
        _ => NotificationSeverity.Info,
    };

    // SQL Server returns DateTime without Kind: they are UTC (we only store UTC)
    public static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
