using SyWater.Notifications.Application.Events;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Domain.Devices;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;

namespace SyWater.Notifications.Application.Tests;

public sealed class FakeAlertRecipients : IPlaceAlertRecipients
{
    public Dictionary<Guid, List<Guid>> ByPlace { get; } = [];
    public bool Down { get; set; }
    public int Calls { get; private set; }

    public Task<IReadOnlyList<Guid>> CollaboratorsWithAlertsAsync(Guid placeId, CancellationToken ct)
    {
        Calls++;
        if (Down) throw new ExternalServiceUnavailableException("ms-places");
        return Task.FromResult<IReadOnlyList<Guid>>(ByPlace.GetValueOrDefault(placeId)?.ToList() ?? []);
    }
}

/// <summary>HU-047 · The collaborators with VIEW_ALERTS hear about the alerts of the place too.</summary>
public class CollaboratorAlertsTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakePlaceDevices _devices = new();
    private readonly FakeNotifications _notifications = new();
    private readonly FakePreferences _prefs = new();
    private readonly FakeOutbox _outbox = new();
    private readonly FakeAlertRecipients _recipients = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly Guid _luis = Guid.NewGuid();
    private readonly Guid _place = Guid.NewGuid();
    private readonly Guid _device = Guid.NewGuid();

    private NotificationDispatcher Dispatcher() => new(_notifications, _prefs, _outbox, new EmailSettings(true, "http://app"), TestPush.Off(), _clock);
    private DeviceEventsHandler Handler() => new(_devices, Dispatcher(), _clock, _recipients);
    private DetectOfflineDevicesUseCase Detector() => new(_devices, Dispatcher(), OfflineSettings.Default, _clock, _recipients);

    private Task Link() =>
        Handler().HandleAsync("evt-link", new DeviceLinkedEvent(_device, "SW-ESP32-000001", _place, _owner, _clock.Now), default);

    private Task Valve(string state, string eventId, int minutes) =>
        Handler().HandleAsync(eventId, new ValveReportedEvent(_device, _place, state, null, _clock.Now.AddMinutes(minutes)), default);

    private IEnumerable<Notification> For(Guid user) => _notifications.All.Where(n => n.Audience.UserId == user);

    [Fact]
    public async Task A_closed_valve_is_a_critical_alert_for_the_owner_and_for_the_collaborators_with_alerts()
    {
        _recipients.ByPlace[_place] = [_ana, _luis];
        await Link();
        await Valve("OPEN", "evt-1", 1);

        await Valve("CLOSED", "evt-2", 2);

        foreach (var user in new[] { _owner, _ana, _luis })
        {
            var alert = Assert.Single(For(user), n => n.Type == NotificationType.ValveChanged && n.Severity == NotificationSeverity.Critical);
            Assert.Equal(_place, alert.PlaceId);
        }
        Assert.Contains("compartido contigo", For(_ana).Single(n => n.Severity == NotificationSeverity.Critical).Body);
        Assert.DoesNotContain("compartido contigo", For(_owner).Single(n => n.Severity == NotificationSeverity.Critical).Body);
    }

    [Fact]
    public async Task Only_alerts_go_to_collaborators_not_the_linking_or_the_reopening()
    {
        _recipients.ByPlace[_place] = [_ana];
        await Link();
        await Valve("CLOSED", "evt-1", 1);
        await Valve("OPEN", "evt-2", 2);

        var types = For(_ana).Select(n => (n.Type, n.Severity)).ToList();
        Assert.Equal([(NotificationType.ValveChanged, NotificationSeverity.Critical)], types);
        Assert.Equal(3, For(_owner).Count());   // linked, closed, opened
    }

    [Fact]
    public async Task A_redelivered_event_does_not_notify_a_collaborator_twice()
    {
        _recipients.ByPlace[_place] = [_ana];
        await Link();
        await Valve("OPEN", "evt-1", 1);

        await Valve("CLOSED", "evt-2", 2);
        await Valve("CLOSED", "evt-2", 2);

        Assert.Single(For(_ana), n => n.Severity == NotificationSeverity.Critical);
    }

    [Fact]
    public async Task Each_collaborator_follows_their_own_channel_preferences()
    {
        _recipients.ByPlace[_place] = [_ana];
        _prefs.Saved[(_ana, NotificationSeverity.Critical)] = NotificationChannels.InApp | NotificationChannels.Email;
        await Link();
        await Valve("OPEN", "evt-1", 1);

        await Valve("CLOSED", "evt-2", 2);

        Assert.Contains(_outbox.All, m => m.UserId == _ana);
    }

    [Fact]
    public async Task The_owner_listed_as_a_recipient_is_not_notified_twice()
    {
        _recipients.ByPlace[_place] = [_owner, _ana];
        await Link();
        await Valve("OPEN", "evt-1", 1);

        await Valve("CLOSED", "evt-2", 2);

        Assert.Single(For(_owner), n => n.Severity == NotificationSeverity.Critical);
    }

    [Fact]
    public async Task Without_collaborators_or_without_the_directory_only_the_owner_is_told()
    {
        await Link();
        await Valve("OPEN", "evt-1", 1);
        await Valve("CLOSED", "evt-2", 2);
        Assert.Single(_notifications.All, n => n.Severity == NotificationSeverity.Critical);

        var alone = new DeviceEventsHandler(_devices, Dispatcher(), _clock);   // ms-places not configured
        await alone.HandleAsync("evt-3", new ValveReportedEvent(_device, _place, "OPEN", null, _clock.Now.AddMinutes(3)), default);
        await alone.HandleAsync("evt-4", new ValveReportedEvent(_device, _place, "CLOSED", null, _clock.Now.AddMinutes(4)), default);
        Assert.Equal(2, _notifications.All.Count(n => n.Severity == NotificationSeverity.Critical));
    }

    [Fact]
    public async Task If_ms_places_is_down_the_event_fails_to_be_retried_after_the_owner_was_told()
    {
        _recipients.ByPlace[_place] = [_ana];
        await Link();
        await Valve("OPEN", "evt-1", 1);
        _recipients.Down = true;

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() => Valve("CLOSED", "evt-2", 2));
        Assert.Single(For(_owner), n => n.Severity == NotificationSeverity.Critical);
        Assert.Empty(For(_ana));

        // The handler saves the new valve state only at the end, so the database still has the OPEN state of before.
        // The in-memory fake shares the instance, so put that stored state back by hand.
        var stored = PlaceDevice.ForNewLink(_place, _device, _owner, "SW-ESP32-000001");
        stored.ApplyValveReport(ValveStatus.Open, DateTime.SpecifyKind(_clock.Now.AddMinutes(1), DateTimeKind.Utc));
        _devices.ByPlace[_place] = stored;

        _recipients.Down = false;   // RabbitMQ delivers it again
        await Valve("CLOSED", "evt-2", 2);

        Assert.Single(For(_owner), n => n.Severity == NotificationSeverity.Critical);   // still one
        Assert.Single(For(_ana), n => n.Severity == NotificationSeverity.Critical);
    }

    [Fact]
    public async Task A_silent_device_alerts_the_collaborators_with_alerts_too_and_only_once()
    {
        _recipients.ByPlace[_place] = [_ana];
        await Link();
        await Handler().HandleAsync("evt-r", new ReadingReceivedEvent(_device, _place, _clock.Now, 1.5m, 0.1m, 100m, _clock.Now), default);
        _clock.Now = _clock.Now.AddMinutes(11);

        Assert.Equal(1, await Detector().ExecuteAsync(default));
        Assert.Equal(0, await Detector().ExecuteAsync(default));

        var offline = _notifications.All.Where(n => n.Type == NotificationType.DeviceOffline).ToList();
        Assert.Equal([_owner, _ana], offline.Select(n => n.Audience.UserId!.Value).OrderBy(id => id == _owner ? 0 : 1).ToArray());
        Assert.All(offline, n => Assert.Equal(NotificationSeverity.Warning, n.Severity));
    }

    [Fact]
    public async Task If_ms_places_is_down_the_offline_alert_is_retried_in_the_next_sweep()
    {
        _recipients.ByPlace[_place] = [_ana];
        await Link();
        await Handler().HandleAsync("evt-r", new ReadingReceivedEvent(_device, _place, _clock.Now, 1.5m, 0.1m, 100m, _clock.Now), default);
        _clock.Now = _clock.Now.AddMinutes(11);
        _recipients.Down = true;

        Assert.Equal(0, await Detector().ExecuteAsync(default));   // not marked as alerted
        Assert.Single(_notifications.All, n => n.Type == NotificationType.DeviceOffline);   // the owner already heard it

        _recipients.Down = false;
        Assert.Equal(1, await Detector().ExecuteAsync(default));

        Assert.Single(For(_owner), n => n.Type == NotificationType.DeviceOffline);   // never twice
        Assert.Single(For(_ana), n => n.Type == NotificationType.DeviceOffline);
    }
}
