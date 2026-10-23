using SyWater.Notifications.Application.Events;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Application.Views;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;

namespace SyWater.Notifications.Application.Tests;

public sealed class FakePreferences : INotificationPreferenceRepository
{
    public Dictionary<(Guid, NotificationSeverity), NotificationChannels> Saved { get; } = [];

    public Task<ChannelPreferences> GetAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult(ChannelPreferences.Restore(Saved.Where(kv => kv.Key.Item1 == userId)
            .ToDictionary(kv => kv.Key.Item2, kv => kv.Value)));

    public Task SaveAsync(Guid userId, NotificationSeverity severity, NotificationChannels channels, DateTime now, CancellationToken ct)
    {
        Saved[(userId, severity)] = channels;
        return Task.CompletedTask;
    }
}

public class PreferenceTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakePreferences _prefs = new();
    private readonly FakeNotifications _notifications = new();
    private readonly Requester _me = new(Guid.NewGuid(), [Roles.User]);

    private UpdateMyPreferencesUseCase Update() => new(_prefs, _clock);
    private readonly FakeOutbox _outbox = new();
    private NotificationDispatcher Dispatcher() => new(_notifications, _prefs, _outbox, new EmailSettings(true, "http://app"), TestPush.Off(), _clock);

    private Notification Personal(NotificationSeverity severity, string? source = "evt-1") =>
        Notification.Create(Audience.ForUser(_me.UserId), NotificationType.ValveChanged, severity, "T", "B", null, source, _clock.Now);

    [Fact]
    public async Task Without_changes_the_user_gets_the_defaults_of_the_channel_matrix()
    {
        var view = await new GetMyPreferencesUseCase(_prefs).ExecuteAsync(_me, default);

        var critical = view.Levels.Single(l => l.Severity == NotificationSeverity.Critical);
        Assert.True(critical.InApp && critical.Push && critical.Email && critical.Sms);
        var warning = view.Levels.Single(l => l.Severity == NotificationSeverity.Warning);
        Assert.True(warning.InApp && warning.Push);
        Assert.False(warning.Email || warning.Sms);
        var info = view.Levels.Single(l => l.Severity == NotificationSeverity.Info);
        Assert.True(info.InApp);
        Assert.False(info.Push || info.Email || info.Sms);
    }

    [Fact]
    public async Task A_change_is_saved_and_shown_back()
    {
        await Update().ExecuteAsync(_me, NotificationSeverity.Critical, true, true, false, false, default);

        var critical = (await new GetMyPreferencesUseCase(_prefs).ExecuteAsync(_me, default))
            .Levels.Single(l => l.Severity == NotificationSeverity.Critical);
        Assert.True(critical.InApp && critical.Push);
        Assert.False(critical.Email || critical.Sms);
    }

    [Fact]
    public async Task Critical_alerts_cannot_lose_the_in_app_channel()
    {
        await Assert.ThrowsAsync<CriticalChannelRequiredException>(() =>
            Update().ExecuteAsync(_me, NotificationSeverity.Critical, false, true, true, true, default));
        await Assert.ThrowsAsync<CriticalChannelRequiredException>(() =>
            Update().ExecuteAsync(_me, NotificationSeverity.Critical, false, false, false, false, default));
        Assert.Empty(_prefs.Saved);
    }

    [Fact]
    public async Task Other_levels_may_turn_everything_off()
    {
        var saved = await Update().ExecuteAsync(_me, NotificationSeverity.Info, false, false, false, false, default);

        Assert.False(saved.InApp || saved.Push || saved.Email || saved.Sms);
    }

    [Fact]
    public async Task Preferences_are_personal()
    {
        await Update().ExecuteAsync(_me, NotificationSeverity.Warning, false, false, false, false, default);

        var other = await _prefs.GetAsync(Guid.NewGuid(), default);
        Assert.Equal(ChannelPreferences.DefaultFor(NotificationSeverity.Warning), other.ChannelsFor(NotificationSeverity.Warning));
    }

    [Fact]
    public async Task The_dispatcher_stores_in_app_and_reports_the_other_channels_to_deliver()
    {
        var external = await Dispatcher().DispatchAsync(Personal(NotificationSeverity.Critical), new DeliveryContext("Lugar de prueba"), default);

        Assert.Single(_notifications.All);
        Assert.Equal(NotificationChannels.Push | NotificationChannels.Sms, external);   // push and SMS are switched off in this dispatcher (see ShortMessageTests for SMS on)
        Assert.Single(_outbox.All);                                                    // the e-mail is queued
    }

    [Fact]
    public async Task The_dispatcher_respects_a_user_who_turned_in_app_off_for_a_non_critical_level()
    {
        await Update().ExecuteAsync(_me, NotificationSeverity.Info, false, false, false, false, default);

        var external = await Dispatcher().DispatchAsync(Personal(NotificationSeverity.Info), new DeliveryContext("Lugar de prueba"), default);

        Assert.Empty(_notifications.All);
        Assert.Equal(NotificationChannels.None, external);
    }

    [Fact]
    public async Task A_changed_preference_applies_to_the_very_next_notification()
    {
        await Update().ExecuteAsync(_me, NotificationSeverity.Warning, true, false, false, false, default);
        Assert.Equal(NotificationChannels.None, await Dispatcher().DispatchAsync(Personal(NotificationSeverity.Warning, "evt-1"), new DeliveryContext("Lugar de prueba"), default));

        await Update().ExecuteAsync(_me, NotificationSeverity.Warning, true, false, true, false, default);
        Assert.Empty(_outbox.All);
        await Dispatcher().DispatchAsync(Personal(NotificationSeverity.Warning, "evt-2"), new DeliveryContext("Lugar de prueba"), default);
        Assert.Single(_outbox.All);   // the e-mail channel was switched on and applies at once
    }

    [Fact]
    public async Task A_redelivered_event_creates_one_notification_and_one_mail()
    {
        await Dispatcher().DispatchAsync(Personal(NotificationSeverity.Critical, "evt-1"), new DeliveryContext("Lugar de prueba"), default);

        var again = await Dispatcher().DispatchAsync(Personal(NotificationSeverity.Critical, "evt-1"), new DeliveryContext("Lugar de prueba"), default);

        Assert.Single(_notifications.All);
        Assert.Single(_outbox.All);   // one mail too
        Assert.Equal(NotificationChannels.Push | NotificationChannels.Sms, again);   // steps are idempotent, so it can safely rerun
    }

    [Fact]
    public async Task A_role_broadcast_ignores_personal_preferences_and_is_always_stored()
    {
        var broadcast = Notification.Create(Audience.ForRole(Roles.Admin), NotificationType.SystemAnnouncement,
            NotificationSeverity.Info, "T", "B", null, null, _clock.Now);

        Assert.Equal(NotificationChannels.None, await Dispatcher().DispatchAsync(broadcast, new DeliveryContext("Lugar de prueba"), default));
        Assert.Single(_notifications.All);
    }

    [Fact]
    public async Task A_valve_closing_event_goes_through_the_dispatcher_with_the_owner_preferences()
    {
        var devices = new FakePlaceDevices();
        var handler = new DeviceEventsHandler(devices, Dispatcher(), _clock);
        var place = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Update().ExecuteAsync(_me, NotificationSeverity.Info, false, false, false, false, default);   // no "linked" notices

        await handler.HandleAsync("evt-1", new DeviceLinkedEvent(device, "SW-1", place, _me.UserId, _clock.Now), default);
        await handler.HandleAsync("evt-2", new ValveReportedEvent(device, place, "CLOSED", null, _clock.Now.AddMinutes(1)), default);

        var only = Assert.Single(_notifications.All);   // the critical one; the Info one was switched off
        Assert.Equal(NotificationSeverity.Critical, only.Severity);
    }
}
