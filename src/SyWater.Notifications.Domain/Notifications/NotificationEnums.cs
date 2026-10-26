namespace SyWater.Notifications.Domain.Notifications;

/// <summary>How urgent a notification is (the app picks the icon and color from it).</summary>
public enum NotificationSeverity { Info, Warning, Critical }

/// <summary>What happened. Stable strings in the database (see NotificationMapper).</summary>
public enum NotificationType
{
    DeviceLinked, DeviceUnlinked, ValveChanged, DeviceOffline, SystemAnnouncement,

    /// <summary>HU-042: a registered user is invited to collaborate in a place.</summary>
    CollaboratorInvitation,

    /// <summary>HU-043: the owner learns that the invited person accepted or rejected.</summary>
    InvitationAnswered,
}

/// <summary>Roles that ms-iam puts in the "roles" claim of the token.</summary>
public static class Roles
{
    public const string User = "USER";
    public const string Admin = "ADMIN";
}
