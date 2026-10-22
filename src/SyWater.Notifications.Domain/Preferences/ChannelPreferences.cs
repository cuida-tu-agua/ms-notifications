using SyWater.Notifications.Domain.Common;
using SyWater.Notifications.Domain.Notifications;

namespace SyWater.Notifications.Domain.Preferences;

/// <summary>Where a notification can reach the user (channel matrix of the backlog, epic E7).</summary>
[Flags]
public enum NotificationChannels
{
    None = 0,
    InApp = 1,
    Push = 2,
    Email = 4,

    /// <summary>WhatsApp / SMS (HU-028).</summary>
    Sms = 8,
    All = InApp | Push | Email | Sms,
}

/// <summary>HU-034: a CRITICAL level cannot lose the in-app channel. HTTP 400.</summary>
public sealed class CriticalChannelRequiredException()
    : DomainException("preferences.critical_requires_in_app", "Critical alerts always keep the in-app notification.");

/// <summary>HU-028: SMS / WhatsApp is only for CRITICAL alerts, so it cannot be turned on for another level. HTTP 400.</summary>
public sealed class SmsOnlyForCriticalException()
    : DomainException("preferences.sms_only_critical", "SMS / WhatsApp can only be turned on for critical alerts.");

/// <summary>
/// The channels a user wants for each urgency level. A level the user never touched uses the default of the
/// backlog matrix (Critical: all · Important/Warning: app + push · Informative: app only).
/// </summary>
public sealed class ChannelPreferences
{
    private readonly Dictionary<NotificationSeverity, NotificationChannels> _custom;

    private ChannelPreferences(Dictionary<NotificationSeverity, NotificationChannels> custom) => _custom = custom;

    public static ChannelPreferences Defaults() => new([]);

    /// <summary>The user's saved levels (rows of the table).</summary>
    public static ChannelPreferences Restore(IReadOnlyDictionary<NotificationSeverity, NotificationChannels> saved) =>
        new(new Dictionary<NotificationSeverity, NotificationChannels>(saved));

    public static NotificationChannels DefaultFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => NotificationChannels.All,
        NotificationSeverity.Warning => NotificationChannels.InApp | NotificationChannels.Push,
        _ => NotificationChannels.InApp,
    };

    /// <summary>SMS / WhatsApp never shows up for a non-critical level, even if an old row still has it.</summary>
    public NotificationChannels ChannelsFor(NotificationSeverity severity)
    {
        var channels = _custom.TryGetValue(severity, out var saved) ? saved : DefaultFor(severity);
        return severity == NotificationSeverity.Critical ? channels : channels & ~NotificationChannels.Sms;
    }

    /// <summary>Applies at once (the dispatcher reads this on every notification).</summary>
    public void Set(NotificationSeverity severity, NotificationChannels channels)
    {
        if (severity == NotificationSeverity.Critical && !channels.HasFlag(NotificationChannels.InApp))
            throw new CriticalChannelRequiredException();
        if (severity != NotificationSeverity.Critical && channels.HasFlag(NotificationChannels.Sms))
            throw new SmsOnlyForCriticalException();
        _custom[severity] = channels & NotificationChannels.All;
    }
}
