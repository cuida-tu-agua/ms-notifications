using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Infrastructure.Persistence;

namespace SyWater.Notifications.Infrastructure.Tests;

/// <summary>The real EF queries against SQLite in memory (the schema comes from the model, like the API tests of the other services).</summary>
public sealed class EfNotificationRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _sqlite = new("DataSource=:memory:");
    private readonly DbContextOptions<NotificationDbContext> _options;
    private readonly Guid _juan = Guid.NewGuid();
    private readonly Guid _ana = Guid.NewGuid();

    public EfNotificationRepositoryTests()
    {
        _sqlite.Open();
        _options = new DbContextOptionsBuilder<NotificationDbContext>().UseSqlite(_sqlite).Options;
        using var db = new NotificationDbContext(_options);
        db.Database.EnsureCreated();
    }

    private EfNotificationRepository Repo() => new(new NotificationDbContext(_options));

    private static Notification Make(Audience audience, int minutesAgo = 0, string? source = null, Guid? place = null) =>
        Notification.Create(audience, NotificationType.ValveChanged, NotificationSeverity.Warning, "Title", "Body",
            place, source, Now.AddMinutes(-minutesAgo));

    [Fact]
    public async Task A_notification_survives_the_round_trip()
    {
        var place = Guid.NewGuid();
        var saved = Make(Audience.ForUser(_juan), source: "evt-1", place: place);
        Assert.True(await Repo().AddAsync(saved, default));

        var read = await Repo().GetAsync(saved.Id, default);

        Assert.NotNull(read);
        Assert.Equal(_juan, read.Audience.UserId);
        Assert.Equal(NotificationType.ValveChanged, read.Type);
        Assert.Equal(NotificationSeverity.Warning, read.Severity);
        Assert.Equal(place, read.PlaceId);
        Assert.Equal("evt-1", read.SourceEventId);
        Assert.Equal(DateTimeKind.Utc, read.CreatedAt.Kind);
    }

    [Fact]
    public async Task The_same_source_event_creates_one_notification_per_audience()
    {
        Assert.True(await Repo().AddAsync(Make(Audience.ForUser(_juan), source: "evt-1"), default));
        Assert.False(await Repo().AddAsync(Make(Audience.ForUser(_juan), source: "evt-1"), default));   // redelivered
        Assert.True(await Repo().AddAsync(Make(Audience.ForUser(_ana), source: "evt-1"), default));     // other audience
        Assert.True(await Repo().AddAsync(Make(Audience.ForRole("ADMIN"), source: "evt-1"), default));
        Assert.False(await Repo().AddAsync(Make(Audience.ForRole("ADMIN"), source: "evt-1"), default));
    }

    [Fact]
    public async Task The_inbox_filters_by_user_and_role_and_pages_newest_first()
    {
        await Repo().AddAsync(Make(Audience.ForUser(_juan), minutesAgo: 3), default);
        await Repo().AddAsync(Make(Audience.ForUser(_ana), minutesAgo: 2), default);
        await Repo().AddAsync(Make(Audience.ForRole("ADMIN"), minutesAgo: 1), default);
        await Repo().AddAsync(Make(Audience.ForRole("USER"), minutesAgo: 0), default);

        var juan = await Repo().ListForAsync(_juan, ["user"], false, null, 10, default);   // roles in any case
        Assert.Equal(2, juan.Count);
        Assert.True(juan[0].Notification.CreatedAt > juan[1].Notification.CreatedAt);

        var admin = await Repo().ListForAsync(_ana, ["USER", "ADMIN"], false, null, 10, default);
        Assert.Equal(3, admin.Count);   // her personal one + ADMIN + USER

        var older = await Repo().ListForAsync(_ana, ["USER", "ADMIN"], false, Now.AddMinutes(-1), 10, default);
        Assert.Single(older);

        Assert.Single(await Repo().ListForAsync(_ana, ["USER", "ADMIN"], false, null, 1, default));
        Assert.Empty(await Repo().ListForAsync(Guid.NewGuid(), [], false, null, 10, default));   // no roles, no personal ones
    }

    [Fact]
    public async Task Reads_are_per_user_even_for_a_role_notification()
    {
        var broadcast = Make(Audience.ForRole("USER"));
        await Repo().AddAsync(broadcast, default);

        var readAt = await Repo().MarkReadAsync(broadcast.Id, _juan, Now, default);

        Assert.Equal(Now, readAt);
        Assert.Equal(0, await Repo().CountUnreadAsync(_juan, ["USER"], default));
        Assert.Equal(1, await Repo().CountUnreadAsync(_ana, ["USER"], default));
        Assert.Empty(await Repo().ListForAsync(_juan, ["USER"], unreadOnly: true, null, 10, default));
        var item = Assert.Single(await Repo().ListForAsync(_juan, ["USER"], false, null, 10, default));
        Assert.Equal(Now, item.ReadAt);
    }

    [Fact]
    public async Task Marking_twice_keeps_the_first_date()
    {
        var n = Make(Audience.ForUser(_juan));
        await Repo().AddAsync(n, default);

        var first = await Repo().MarkReadAsync(n.Id, _juan, Now, default);
        var second = await Repo().MarkReadAsync(n.Id, _juan, Now.AddHours(1), default);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Mark_all_read_marks_only_what_the_user_sees_and_is_idempotent()
    {
        await Repo().AddAsync(Make(Audience.ForUser(_juan)), default);
        await Repo().AddAsync(Make(Audience.ForRole("USER")), default);
        await Repo().AddAsync(Make(Audience.ForRole("ADMIN")), default);
        await Repo().AddAsync(Make(Audience.ForUser(_ana)), default);

        Assert.Equal(2, await Repo().MarkAllReadAsync(_juan, ["USER"], Now, default));
        Assert.Equal(0, await Repo().MarkAllReadAsync(_juan, ["USER"], Now, default));
        Assert.Equal(0, await Repo().CountUnreadAsync(_juan, ["USER"], default));
        Assert.Equal(2, await Repo().CountUnreadAsync(_ana, ["USER"], default));
    }

    public void Dispose() => _sqlite.Dispose();
}

public sealed class EfPlaceDeviceRepositoryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _sqlite = new("DataSource=:memory:");
    private readonly DbContextOptions<NotificationDbContext> _options;
    private readonly Guid _place = Guid.NewGuid();
    private readonly Guid _device = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    public EfPlaceDeviceRepositoryTests()
    {
        _sqlite.Open();
        _options = new DbContextOptionsBuilder<NotificationDbContext>().UseSqlite(_sqlite).Options;
        using var db = new NotificationDbContext(_options);
        db.Database.EnsureCreated();
    }

    private EfPlaceDeviceRepository Repo() => new(new NotificationDbContext(_options));

    [Fact]
    public async Task Link_report_and_unlink_round_trip()
    {
        await Repo().ReplaceAsync(SyWater.Notifications.Domain.Devices.PlaceDevice.ForNewLink(_place, _device, _user, "SW-1"), T0, default);
        var device = await Repo().GetByPlaceAsync(_place, default);
        Assert.NotNull(device);
        Assert.Equal(_user, device.UserId);
        Assert.Null(device.ValveState);

        device.ApplyValveReport(SyWater.Notifications.Domain.Devices.ValveStatus.Closed, T0.AddMinutes(1));
        await Repo().SaveValveReportAsync(device, default);
        var saved = await Repo().GetByPlaceAsync(_place, default);
        Assert.Equal(SyWater.Notifications.Domain.Devices.ValveStatus.Closed, saved!.ValveState);
        Assert.Equal(DateTimeKind.Utc, saved.ValveReportedAt!.Value.Kind);

        await Repo().RemoveAsync(_place, Guid.NewGuid(), default);   // another device: nothing happens
        Assert.NotNull(await Repo().GetByPlaceAsync(_place, default));
        await Repo().RemoveAsync(_place, _device, default);
        Assert.Null(await Repo().GetByPlaceAsync(_place, default));
    }

    [Fact]
    public async Task Linking_again_replaces_the_row_and_an_older_report_does_not_overwrite_a_newer_one()
    {
        await Repo().ReplaceAsync(SyWater.Notifications.Domain.Devices.PlaceDevice.ForNewLink(_place, _device, _user, "SW-1"), T0, default);
        var newOwner = Guid.NewGuid();
        await Repo().ReplaceAsync(SyWater.Notifications.Domain.Devices.PlaceDevice.ForNewLink(_place, _device, newOwner, "SW-1"), T0, default);
        var device = await Repo().GetByPlaceAsync(_place, default);
        Assert.Equal(newOwner, device!.UserId);

        device.ApplyValveReport(SyWater.Notifications.Domain.Devices.ValveStatus.Closed, T0.AddMinutes(5));
        await Repo().SaveValveReportAsync(device, default);
        var stale = SyWater.Notifications.Domain.Devices.PlaceDevice.Restore(_place, _device, newOwner, "SW-1",
            SyWater.Notifications.Domain.Devices.ValveStatus.Open, T0.AddMinutes(1));
        await Repo().SaveValveReportAsync(stale, default);

        Assert.Equal(SyWater.Notifications.Domain.Devices.ValveStatus.Closed, (await Repo().GetByPlaceAsync(_place, default))!.ValveState);
    }

    public void Dispose() => _sqlite.Dispose();
}

public sealed class EfNotificationPreferenceRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _sqlite = new("DataSource=:memory:");
    private readonly DbContextOptions<NotificationDbContext> _options;
    private readonly Guid _user = Guid.NewGuid();

    public EfNotificationPreferenceRepositoryTests()
    {
        _sqlite.Open();
        _options = new DbContextOptionsBuilder<NotificationDbContext>().UseSqlite(_sqlite).Options;
        using var db = new NotificationDbContext(_options);
        db.Database.EnsureCreated();
    }

    private EfNotificationPreferenceRepository Repo() => new(new NotificationDbContext(_options));

    [Fact]
    public async Task Nothing_saved_means_the_defaults_and_a_save_overrides_only_that_level()
    {
        var before = await Repo().GetAsync(_user, default);
        Assert.Equal(SyWater.Notifications.Domain.Preferences.NotificationChannels.All, before.ChannelsFor(NotificationSeverity.Critical));

        await Repo().SaveAsync(_user, NotificationSeverity.Warning, SyWater.Notifications.Domain.Preferences.NotificationChannels.Email, Now, default);

        var after = await Repo().GetAsync(_user, default);
        Assert.Equal(SyWater.Notifications.Domain.Preferences.NotificationChannels.Email, after.ChannelsFor(NotificationSeverity.Warning));
        Assert.Equal(SyWater.Notifications.Domain.Preferences.NotificationChannels.All, after.ChannelsFor(NotificationSeverity.Critical));
        Assert.Equal(SyWater.Notifications.Domain.Preferences.NotificationChannels.InApp, after.ChannelsFor(NotificationSeverity.Info));
    }

    [Fact]
    public async Task Saving_twice_updates_the_same_row_and_other_users_are_not_touched()
    {
        var channels = SyWater.Notifications.Domain.Preferences.NotificationChannels.InApp;
        await Repo().SaveAsync(_user, NotificationSeverity.Info, SyWater.Notifications.Domain.Preferences.NotificationChannels.None, Now, default);
        await Repo().SaveAsync(_user, NotificationSeverity.Info, channels | SyWater.Notifications.Domain.Preferences.NotificationChannels.Push, Now, default);

        Assert.Equal(channels | SyWater.Notifications.Domain.Preferences.NotificationChannels.Push,
            (await Repo().GetAsync(_user, default)).ChannelsFor(NotificationSeverity.Info));
        Assert.Equal(channels, (await Repo().GetAsync(Guid.NewGuid(), default)).ChannelsFor(NotificationSeverity.Info));
    }

    public void Dispose() => _sqlite.Dispose();
}

public sealed class EfPlaceDeviceLivenessTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _sqlite = new("DataSource=:memory:");
    private readonly DbContextOptions<NotificationDbContext> _options;
    private readonly Guid _place = Guid.NewGuid();
    private readonly Guid _device = Guid.NewGuid();

    public EfPlaceDeviceLivenessTests()
    {
        _sqlite.Open();
        _options = new DbContextOptionsBuilder<NotificationDbContext>().UseSqlite(_sqlite).Options;
        using var db = new NotificationDbContext(_options);
        db.Database.EnsureCreated();
    }

    private EfPlaceDeviceRepository Repo() => new(new NotificationDbContext(_options));

    private async Task<SyWater.Notifications.Domain.Devices.PlaceDevice> LinkedWithReadingAt(DateTime at)
    {
        await Repo().ReplaceAsync(SyWater.Notifications.Domain.Devices.PlaceDevice.ForNewLink(_place, _device, Guid.NewGuid(), "SW-1"), T0, default);
        var device = (await Repo().GetByPlaceAsync(_place, default))!;
        device.ApplyReading(at);
        await Repo().SaveReadingAsync(device, default);
        return device;
    }

    [Fact]
    public async Task Only_devices_that_reported_and_went_silent_without_an_alert_are_found()
    {
        await LinkedWithReadingAt(T0);

        Assert.Empty(await Repo().FindSilentAsync(T0.AddMinutes(-1), 10, default));        // heard more recently than the cutoff
        var silent = Assert.Single(await Repo().FindSilentAsync(T0.AddMinutes(10), 10, default));
        Assert.Equal(DateTimeKind.Utc, silent.LastReadingAt!.Value.Kind);

        Assert.True(await Repo().MarkOfflineAlertedAsync(silent, T0.AddMinutes(10), default));
        Assert.Empty(await Repo().FindSilentAsync(T0.AddMinutes(10), 10, default));         // ONE alert
        Assert.False(await Repo().MarkOfflineAlertedAsync(silent, T0.AddMinutes(11), default));   // a second sweeper changes nothing
    }

    [Fact]
    public async Task A_device_that_never_reported_is_never_found()
    {
        await Repo().ReplaceAsync(SyWater.Notifications.Domain.Devices.PlaceDevice.ForNewLink(_place, _device, Guid.NewGuid(), "SW-1"), T0, default);

        Assert.Empty(await Repo().FindSilentAsync(T0.AddDays(5), 10, default));
    }

    [Fact]
    public async Task A_new_reading_clears_the_alert_and_a_reading_during_the_alert_prevents_marking()
    {
        var device = await LinkedWithReadingAt(T0);
        var stale = (await Repo().FindSilentAsync(T0.AddMinutes(10), 10, default)).Single();   // sweeper read it…

        device.ApplyReading(T0.AddMinutes(10));                                                  // …and the device reported meanwhile
        await Repo().SaveReadingAsync(device, default);
        Assert.False(await Repo().MarkOfflineAlertedAsync(stale, T0.AddMinutes(10), default));  // not marked: it is alive

        var alive = (await Repo().GetByPlaceAsync(_place, default))!;
        Assert.True(await Repo().MarkOfflineAlertedAsync(alive, T0.AddMinutes(20), default));
        alive.ApplyReading(T0.AddMinutes(25));
        await Repo().SaveReadingAsync(alive, default);
        Assert.Null((await Repo().GetByPlaceAsync(_place, default))!.OfflineAlertedAt);          // reconnecting re-arms it
    }

    [Fact]
    public async Task An_older_reading_never_overwrites_a_newer_one()
    {
        var device = await LinkedWithReadingAt(T0.AddMinutes(5));
        var older = SyWater.Notifications.Domain.Devices.PlaceDevice.Restore(_place, _device, device.UserId, "SW-1", null, null, T0, null);

        await Repo().SaveReadingAsync(older, default);

        Assert.Equal(T0.AddMinutes(5), (await Repo().GetByPlaceAsync(_place, default))!.LastReadingAt);
    }

    public void Dispose() => _sqlite.Dispose();
}
