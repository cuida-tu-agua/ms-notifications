using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Domain.Emails;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.Tests;

public sealed class FakeSmsOutbox : ISmsOutboxRepository
{
    public List<SmsOutboxItem> All { get; } = [];

    public Task<bool> AddAsync(SmsOutboxItem item, CancellationToken ct)
    {
        if (item.SourceEventId is not null && All.Any(i => i.SourceEventId == item.SourceEventId && i.UserId == item.UserId))
            return Task.FromResult(false);
        All.Add(item);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<SmsOutboxItem>> GetDueAsync(DateTime now, int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SmsOutboxItem>>(All.Where(i => i.IsDue(now)).Take(max).ToList());

    public Task<bool> SaveOutcomeAsync(SmsOutboxItem item, CancellationToken ct) => Task.FromResult(true);   // same instance in memory
}

public sealed class FakeShortMessageSender : IShortMessageSender
{
    public List<ShortMessage> Sent { get; } = [];
    public bool Down { get; set; }
    public string? RejectWith { get; set; }

    public Task<string?> SendAsync(ShortMessage message, CancellationToken ct)
    {
        if (Down) throw new ShortMessageSendException("Twilio 503");
        if (RejectWith is not null) throw new ShortMessageRejectedException(RejectWith, "21614");
        Sent.Add(message);
        return Task.FromResult<string?>($"SM{Sent.Count}");
    }
}

public class ShortMessageDispatchTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeNotifications _notifications = new();
    private readonly FakePreferences _prefs = new();
    private readonly FakeSmsOutbox _sms = new();
    private readonly Guid _user = Guid.NewGuid();

    private NotificationDispatcher Dispatcher(bool smsOn = true) =>
        new(_notifications, _prefs, new FakeOutbox(), new EmailSettings(false, "http://app"), TestPush.Off(), _clock, _sms, new SmsSettings(smsOn));

    private Notification Alert(NotificationSeverity severity, string eventId = "evt-1") =>
        Notification.Create(Audience.ForUser(_user), NotificationType.ValveChanged, severity, "Válvula cerrada",
            "Revisa el lugar.", Guid.NewGuid(), eventId, _clock.Now);

    [Fact]
    public async Task A_critical_alert_is_queued_once_with_the_place_and_the_kind_of_emergency()
    {
        var context = new DeliveryContext("Casa centro");

        var notDelivered = await Dispatcher().DispatchAsync(Alert(NotificationSeverity.Critical), context, default);
        await Dispatcher().DispatchAsync(Alert(NotificationSeverity.Critical), context, default);   // redelivered event

        var item = Assert.Single(_sms.All);
        Assert.Equal(_user, item.UserId);
        Assert.Contains("Válvula cerrada", item.Body);
        Assert.Contains("Casa centro", item.Body);
        Assert.True(item.Body.Length <= GsmText.SegmentLength);
        Assert.False(notDelivered.HasFlag(NotificationChannels.Sms));
    }

    [Theory]
    [InlineData(NotificationSeverity.Warning)]
    [InlineData(NotificationSeverity.Info)]
    public async Task Only_critical_alerts_go_by_sms_even_if_the_user_turned_it_on(NotificationSeverity severity)
    {
        _prefs.Saved[(_user, severity)] = NotificationChannels.All;

        await Dispatcher().DispatchAsync(Alert(severity), new DeliveryContext("Casa"), default);

        Assert.Empty(_sms.All);
    }

    [Fact]
    public async Task A_user_who_turned_sms_off_for_critical_alerts_gets_none()
    {
        _prefs.Saved[(_user, NotificationSeverity.Critical)] = NotificationChannels.InApp | NotificationChannels.Email;

        await Dispatcher().DispatchAsync(Alert(NotificationSeverity.Critical), new DeliveryContext("Casa"), default);

        Assert.Empty(_sms.All);
    }

    [Fact]
    public async Task With_the_channel_off_nothing_is_queued_and_it_is_reported_as_not_delivered()
    {
        var notDelivered = await Dispatcher(smsOn: false).DispatchAsync(Alert(NotificationSeverity.Critical), new DeliveryContext("Casa"), default);

        Assert.Empty(_sms.All);
        Assert.True(notDelivered.HasFlag(NotificationChannels.Sms));
    }

    [Fact]
    public async Task A_broadcast_to_a_role_never_goes_by_sms()
    {
        var broadcast = Notification.Create(Audience.ForRole(Roles.Admin), NotificationType.SystemAnnouncement, NotificationSeverity.Critical,
            "Mantenimiento", "Esta noche.", null, "evt-9", _clock.Now);

        await Dispatcher().DispatchAsync(broadcast, new DeliveryContext(), default);

        Assert.Empty(_sms.All);
    }
}

public class SendDueShortMessagesTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeSmsOutbox _outbox = new();
    private readonly FakeContacts _contacts = new();
    private readonly FakeShortMessageSender _sender = new();
    private readonly Guid _user = Guid.NewGuid();

    private SendDueShortMessagesUseCase Send() => new(_outbox, _contacts, _sender, SmsRetryPolicy.Default, _clock);

    private SmsOutboxItem Queued(string body = "Cuida Tu Agua - ALERTA: Válvula cerrada en Casa.")
    {
        var item = SmsOutboxItem.Queue(_user, $"evt-{_outbox.All.Count}", body, _clock.Now);
        _outbox.All.Add(item);
        return item;
    }

    private void Verified(string phone, ShortMessageChannel channel) =>
        _contacts.Known[_user] = new UserContact(_user, "juan@example.com", "Juan", phone, true, channel);

    [Fact]
    public async Task It_goes_to_the_verified_phone_by_the_mode_it_was_verified_with()
    {
        Verified("+593991112233", ShortMessageChannel.WhatsApp);
        var item = Queued();

        Assert.Equal(1, await Send().ExecuteAsync(default));

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal("+593991112233", sent.ToPhone);
        Assert.Equal(ShortMessageChannel.WhatsApp, sent.Channel);
        Assert.Equal(SmsStatus.Sent, item.Status);
        Assert.Equal("SM1", item.ProviderMessageId);
    }

    [Fact]
    public async Task Without_a_verified_phone_it_is_skipped_and_never_retried()
    {
        _contacts.Known[_user] = new UserContact(_user, "juan@example.com", "Juan", "+573001112233", false, ShortMessageChannel.Sms);
        var notVerified = Queued();
        var unknownUser = SmsOutboxItem.Queue(Guid.NewGuid(), "evt-x", "Hola", _clock.Now);
        _outbox.All.Add(unknownUser);

        Assert.Equal(0, await Send().ExecuteAsync(default));

        Assert.Empty(_sender.Sent);
        Assert.Equal(SmsStatus.Skipped, notVerified.Status);
        Assert.Equal(SmsStatus.Skipped, unknownUser.Status);
    }

    [Fact]
    public async Task A_provider_that_is_down_retries_later_and_a_refused_number_fails_at_once()
    {
        Verified("+573001112233", ShortMessageChannel.Sms);
        var down = Queued();
        _sender.Down = true;
        await Send().ExecuteAsync(default);
        Assert.Equal(SmsStatus.Pending, down.Status);
        Assert.Equal(_clock.Now.AddMinutes(1), down.NextAttemptAt);

        _sender.Down = false;
        _sender.RejectWith = "Twilio 400 (21614): not a mobile number";
        var refused = Queued();
        await Send().ExecuteAsync(default);
        Assert.Equal(SmsStatus.Failed, refused.Status);
        Assert.Equal(ShortMessageChannel.Sms, refused.Channel);
    }

    [Fact]
    public async Task Ms_iam_down_retries_later()
    {
        _contacts.Down = true;
        var item = Queued();

        await Send().ExecuteAsync(default);

        Assert.Equal(SmsStatus.Pending, item.Status);
        Assert.Equal(1, item.Attempts);
    }
}

public class SendVerificationCodeTests
{
    private readonly FakeShortMessageSender _sender = new();

    private SendVerificationCodeUseCase UseCase(bool on = true) => new(_sender, new SmsSettings(on));

    [Fact]
    public async Task The_code_goes_at_once_by_the_mode_the_user_picked()
    {
        var id = await UseCase().ExecuteAsync("+573001112233", "whatsapp", "123456", 5, default);

        Assert.Equal("SM1", id);
        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(ShortMessageChannel.WhatsApp, sent.Channel);
        Assert.Contains("123456", sent.Body);
        Assert.Contains("5 minutos", sent.Body);
        Assert.True(GsmText.ForSms(sent.Body).Length <= GsmText.SegmentLength);
    }

    [Theory]
    [InlineData("3001112233", "SMS", "123456", 5)]       // no country code
    [InlineData("+573001112233", "EMAIL", "123456", 5)]  // unknown mode
    [InlineData("+573001112233", null, "123456", 5)]
    [InlineData("+573001112233", "SMS", "12345", 5)]     // 5 digits
    [InlineData("+573001112233", "SMS", "12a456", 5)]
    [InlineData("+573001112233", "SMS", "123456", 0)]
    [InlineData("+573001112233", "SMS", "123456", 61)]
    public async Task A_request_that_cannot_be_valid_is_400_and_nothing_is_sent(string to, string? channel, string code, int minutes)
    {
        await Assert.ThrowsAsync<InvalidShortMessageException>(() => UseCase().ExecuteAsync(to, channel, code, minutes, default));
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task With_the_channel_off_it_says_so()
    {
        await Assert.ThrowsAsync<ShortMessagesDisabledException>(() => UseCase(on: false).ExecuteAsync("+573001112233", "SMS", "123456", 5, default));
    }

    [Fact]
    public async Task A_number_the_provider_refuses_is_reported_to_ms_iam()
    {
        _sender.RejectWith = "Twilio 400 (63003): not on WhatsApp";
        var error = await Assert.ThrowsAsync<ShortMessageRejectedException>(() => UseCase().ExecuteAsync("+573001112233", "WHATSAPP", "123456", 5, default));
        Assert.Equal("21614", error.ProviderCode);
    }
}
