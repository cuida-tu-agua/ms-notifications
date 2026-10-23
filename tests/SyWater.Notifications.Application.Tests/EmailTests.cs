using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Domain.Emails;
using SyWater.Notifications.Domain.Notifications;

namespace SyWater.Notifications.Application.Tests;

public class EmailTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
    private readonly FakeOutbox _outbox = new();
    private readonly FakeContacts _contacts = new();
    private readonly FakeSender _sender = new();
    private readonly Guid _user = Guid.NewGuid();

    public EmailTests() => _contacts.Known[_user] = new UserContact(_user, "juan@example.com", "Juan Ome");

    private SendDueEmailsUseCase Send() => new(_outbox, _contacts, _sender, EmailRetryPolicy.Default, _clock);

    private EmailOutboxItem Queued(string? source = "evt-1")
    {
        var item = EmailOutboxItem.Queue(_user, source, "Subject", "text", "<p>html</p>", _clock.Now);
        _outbox.All.Add(item);
        return item;
    }

    [Fact]
    public void The_mail_says_place_kind_time_what_to_do_and_links_to_the_app()
    {
        var place = Guid.NewGuid();
        var n = Notification.Create(Audience.ForUser(_user), NotificationType.ValveChanged, NotificationSeverity.Critical,
            "Válvula cerrada", "Revisa el lugar y abre la válvula.", place, "evt-1", _clock.Now);

        var (subject, text, html) = EmailComposer.Compose(n, new DeliveryContext("Lugar con el dispositivo SW-1"), "http://app/");

        Assert.Contains("ALERTA CRÍTICA", subject);
        Assert.Contains("Lugar con el dispositivo SW-1", text);
        Assert.Contains("Estado de la válvula", text);
        Assert.Contains("2026-10-07 07:00 (hora Colombia)", text);
        Assert.Contains("Revisa el lugar y abre la válvula.", text);
        Assert.Contains($"http://app/places/{place}", text);
        Assert.Contains($"href=\"http://app/places/{place}\"", html);
    }

    [Fact]
    public void The_html_is_escaped()
    {
        var n = Notification.Create(Audience.ForUser(_user), NotificationType.SystemAnnouncement, NotificationSeverity.Info,
            "<script>x</script>", "a & b", null, null, _clock.Now);

        var (_, _, html) = EmailComposer.Compose(n, new DeliveryContext(), "http://app");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("a &amp; b", html);
    }

    [Fact]
    public async Task A_due_mail_is_sent_to_the_address_ms_iam_gives()
    {
        var item = Queued();

        Assert.Equal(1, await Send().ExecuteAsync(default));

        var mail = Assert.Single(_sender.Sent);
        Assert.Equal("juan@example.com", mail.ToAddress);
        Assert.Equal("Juan Ome", mail.ToName);
        Assert.Equal(EmailStatus.Sent, item.Status);
        Assert.Equal(0, await Send().ExecuteAsync(default));   // it does not leave twice
    }

    [Fact]
    public async Task When_the_smtp_server_is_down_the_mail_is_retried_later_and_finally_leaves()
    {
        var item = Queued();
        _sender.Down = true;

        Assert.Equal(0, await Send().ExecuteAsync(default));
        Assert.Equal(EmailStatus.Pending, item.Status);
        Assert.Equal(1, item.Attempts);
        Assert.Equal(_clock.Now.AddMinutes(1), item.NextAttemptAt);
        Assert.Equal(0, await Send().ExecuteAsync(default));   // not due yet: no new attempt
        Assert.Equal(1, item.Attempts);

        _sender.Down = false;
        _clock.Now = _clock.Now.AddMinutes(2);
        Assert.Equal(1, await Send().ExecuteAsync(default));
        Assert.Equal(EmailStatus.Sent, item.Status);
    }

    [Fact]
    public async Task After_the_last_attempt_the_mail_is_failed_for_good()
    {
        var item = Queued();
        _sender.Down = true;

        for (var i = 0; i < EmailRetryPolicy.Default.MaxAttempts; i++)
        {
            await Send().ExecuteAsync(default);
            _clock.Now = _clock.Now.AddHours(2);
        }

        Assert.Equal(EmailStatus.Failed, item.Status);
        Assert.Equal(EmailRetryPolicy.Default.MaxAttempts, item.Attempts);
        Assert.NotNull(item.LastError);
        _sender.Down = false;
        Assert.Equal(0, await Send().ExecuteAsync(default));   // final: never retried again
    }

    [Fact]
    public async Task When_ms_iam_is_down_the_mail_waits_and_when_the_user_has_no_address_it_is_discarded()
    {
        var waiting = Queued("evt-1");
        _contacts.Down = true;
        await Send().ExecuteAsync(default);
        Assert.Equal(EmailStatus.Pending, waiting.Status);

        _contacts.Down = false;
        _contacts.Known.Remove(_user);
        _clock.Now = _clock.Now.AddMinutes(2);
        await Send().ExecuteAsync(default);

        Assert.Equal(EmailStatus.Failed, waiting.Status);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task One_bad_mail_does_not_stop_the_others()
    {
        var gone = Guid.NewGuid();
        _outbox.All.Add(EmailOutboxItem.Queue(gone, "evt-a", "S", "t", "h", _clock.Now));   // user unknown to ms-iam
        Queued("evt-b");

        Assert.Equal(1, await Send().ExecuteAsync(default));
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task The_mail_channel_off_queues_nothing_and_info_alerts_do_not_mail_by_default()
    {
        var notifications = new FakeNotifications();
        var prefs = new FakePreferences();
        var off = new NotificationDispatcher(notifications, prefs, _outbox, new EmailSettings(false, "http://app"), TestPush.Off(), _clock);
        var on = new NotificationDispatcher(notifications, prefs, _outbox, new EmailSettings(true, "http://app"), TestPush.Off(), _clock);
        Notification Make(NotificationSeverity s, string src) => Notification.Create(Audience.ForUser(_user),
            NotificationType.ValveChanged, s, "T", "B", null, src, _clock.Now);

        await off.DispatchAsync(Make(NotificationSeverity.Critical, "e1"), new DeliveryContext(), default);
        await on.DispatchAsync(Make(NotificationSeverity.Info, "e2"), new DeliveryContext(), default);
        await on.DispatchAsync(Make(NotificationSeverity.Warning, "e3"), new DeliveryContext(), default);
        Assert.Empty(_outbox.All);

        await on.DispatchAsync(Make(NotificationSeverity.Critical, "e4"), new DeliveryContext(), default);
        Assert.Single(_outbox.All);
    }
}
