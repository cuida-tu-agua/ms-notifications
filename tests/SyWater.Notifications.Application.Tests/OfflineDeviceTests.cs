using SyWater.Notifications.Application.Events;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;

namespace SyWater.Notifications.Application.Tests;

public class OfflineDeviceTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakePlaceDevices _devices = new();
    private readonly FakeNotifications _notifications = new();
    private readonly FakePreferences _prefs = new();
    private readonly FakeOutbox _outbox = new();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _place = Guid.NewGuid();
    private readonly Guid _device = Guid.NewGuid();

    private NotificationDispatcher Dispatcher() => new(_notifications, _prefs, _outbox, new EmailSettings(true, "http://app"), TestPush.Off(), _clock);
    private DeviceEventsHandler Handler() => new(_devices, Dispatcher(), _clock);
      private DetectOfflineDevicesUseCase Detector() => new(_devices, Dispatcher(), new OfflineSettings(TimeSpan.FromMinutes(10)), _clock);

    /// <summary>A reading received <paramref name="minutesFromStart"/> after the test started (links the device the first time).</summary>
    private async Task ReadingAt(int minutesFromStart)
    {
        var start = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        if (!_devices.ByPlace.ContainsKey(_place))
            await Handler().HandleAsync("evt-link", new DeviceLinkedEvent(_device, "SW-ESP32-000001", _place, _user, start), default);
        await Handler().HandleAsync($"evt-r{minutesFromStart}", new ReadingReceivedEvent(
            _device, _place, start, 1.5m, 0.1m, 100m, start.AddMinutes(minutesFromStart)), default);
    }

    private void Advance(int minutes) => _clock.Now = _clock.Now.AddMinutes(minutes);

    private IEnumerable<Notification> OfflineAlerts => _notifications.All.Where(n => n.Type == NotificationType.DeviceOffline);

    [Fact]
    public async Task A_device_that_stays_silent_for_ten_minutes_alerts_its_owner_once()
    {
        await ReadingAt(0);
        Advance(9);
        Assert.Equal(0, await Detector().ExecuteAsync(default));

        Advance(2);   // 11 minutes of silence
        Assert.Equal(1, await Detector().ExecuteAsync(default));
        Assert.Equal(0, await Detector().ExecuteAsync(default));   // next sweeps: nothing new

        var alert = Assert.Single(OfflineAlerts);
        Assert.Equal(_user, alert.Audience.UserId);
        Assert.Equal(NotificationSeverity.Warning, alert.Severity);   // important: app + push
        Assert.Equal(_place, alert.PlaceId);
        Assert.Contains("2026-10-07 07:00 (hora Colombia)", alert.Body);   // says when it was last heard
        Assert.Contains("SW-ESP32-000001", alert.Body);
        Assert.Empty(_outbox.All);                                    // important alerts are not mailed by default
    }

    [Fact]
    public async Task After_reconnecting_a_new_silence_alerts_again()
    {
        await ReadingAt(0);
        Advance(11);
        await Detector().ExecuteAsync(default);

        await ReadingAt(12);   // it is back
        Advance(12);           // and silent again
        Assert.Equal(1, await Detector().ExecuteAsync(default));

        Assert.Equal(2, OfflineAlerts.Count());
    }

    [Fact]
    public async Task A_device_that_keeps_reporting_never_alerts()
    {
        await ReadingAt(0);
        for (var minute = 1; minute <= 30; minute++)
        {
            await ReadingAt(minute);
            Advance(1);
            Assert.Equal(0, await Detector().ExecuteAsync(default));
        }
        Assert.Empty(OfflineAlerts);
    }

    [Fact]
    public async Task A_device_that_never_reported_or_was_unlinked_does_not_alert()
    {
        await Handler().HandleAsync("evt-link", new DeviceLinkedEvent(_device, "SW-1", _place, _user, _clock.Now), default);
        Advance(300);
        Assert.Equal(0, await Detector().ExecuteAsync(default));

        await Handler().HandleAsync("evt-r", new ReadingReceivedEvent(_device, _place, _clock.Now, 0, 0, 0, _clock.Now), default);
        await Handler().HandleAsync("evt-un", new DeviceUnlinkedEvent(_device, "SW-1", _place, _user, _clock.Now), default);
        Advance(300);
        Assert.Equal(0, await Detector().ExecuteAsync(default));
        Assert.Empty(OfflineAlerts);
    }

    [Fact]
    public async Task Readings_of_another_device_do_not_keep_this_one_alive()
    {
        await ReadingAt(0);
        await Handler().HandleAsync("evt-x", new ReadingReceivedEvent(Guid.NewGuid(), _place, _clock.Now, 0, 0, 0, _clock.Now.AddMinutes(8)), default);

        Advance(11);

        Assert.Equal(1, await Detector().ExecuteAsync(default));
        Assert.Single(OfflineAlerts);
    }

    [Fact]
    public async Task A_user_who_switched_the_in_app_channel_off_for_important_alerts_is_respected()
    {
        await ReadingAt(0);
        await _prefs.SaveAsync(_user, NotificationSeverity.Warning, NotificationChannels.None, _clock.Now, default);
        Advance(11);

        Assert.Equal(1, await Detector().ExecuteAsync(default));   // marked as alerted, so it does not loop
        Assert.Empty(OfflineAlerts);
    }
}
