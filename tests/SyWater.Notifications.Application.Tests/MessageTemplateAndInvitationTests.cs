using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Domain.Emails;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.Tests;

public sealed class FakeTemplates : IMessageTemplateRepository
{
    public Dictionary<MessageTemplateKey, MessageTemplate> Saved { get; } = [];
    public bool Down { get; set; }

    public Task<MessageTemplate?> FindCustomAsync(MessageTemplateKey key, CancellationToken ct) =>
        Down ? throw new InvalidOperationException("database down") : Task.FromResult(Saved.GetValueOrDefault(key));

    public Task<IReadOnlyList<MessageTemplate>> ListCustomAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MessageTemplate>>(Saved.Values.ToList());

    public Task SaveAsync(MessageTemplate template, CancellationToken ct)
    {
        Saved[template.Key] = template;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(MessageTemplateKey key, CancellationToken ct) => Task.FromResult(Saved.Remove(key));
}

public class CustomTextsTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc));
    private readonly FakeTemplates _templates = new();
    private readonly FakeSmsOutbox _sms = new();
    private readonly Guid _user = Guid.NewGuid();

    private NotificationDispatcher Dispatcher() =>
        new(new FakeNotifications(), new FakePreferences(), new FakeOutbox(), new EmailSettings(false, "http://app"), TestPush.Off(),
            _clock, _sms, new SmsSettings(true), new MessageTexts(_templates));

    private Notification Alert() =>
        Notification.Create(Audience.ForUser(_user), NotificationType.ValveChanged, NotificationSeverity.Critical, "Válvula cerrada",
            "Revisa el lugar.", Guid.NewGuid(), "evt-1", _clock.Now);

    [Fact]
    public async Task A_critical_alert_is_written_with_the_text_the_admin_saved()
    {
        _templates.Saved[MessageTemplateKey.AlertCritical] =
            MessageTemplate.Custom(MessageTemplateKey.AlertCritical, "URGENTE {place}: {title}. Llama al plomero.", Guid.NewGuid(), _clock.Now);

        await Dispatcher().DispatchAsync(Alert(), new DeliveryContext("Casa centro"), default);

        Assert.Equal("URGENTE Casa centro: Válvula cerrada. Llama al plomero.", Assert.Single(_sms.All).Body);
    }

    [Fact]
    public async Task If_the_custom_text_cannot_be_read_the_alert_still_leaves_with_the_default_one()
    {
        _templates.Down = true;

        await Dispatcher().DispatchAsync(Alert(), new DeliveryContext("Casa centro"), default);

        Assert.StartsWith("Cuida Tu Agua - ALERTA: Válvula cerrada en Casa centro", Assert.Single(_sms.All).Body);
    }

    [Fact]
    public async Task The_phone_code_uses_the_custom_text_and_tells_the_provider_it_is_a_code()
    {
        _templates.Saved[MessageTemplateKey.PhoneCode] =
            MessageTemplate.Custom(MessageTemplateKey.PhoneCode, "Código {code} ({minutes} min).", Guid.NewGuid(), _clock.Now);
        var sender = new FakeShortMessageSender();

        await new SendVerificationCodeUseCase(sender, new SmsSettings(true), new MessageTexts(_templates))
            .ExecuteAsync("+573001112233", "WHATSAPP", "654321", 5, default);

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("Código 654321 (5 min).", sent.Body);
        Assert.Equal(ShortMessageKind.VerificationCode, sent.Kind);
        Assert.Equal("654321", sent.Code);
    }
}

public class MessageTemplateUseCaseTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc));
    private readonly FakeTemplates _templates = new();
    private readonly Guid _admin = Guid.NewGuid();

    [Fact]
    public async Task The_list_has_every_message_with_its_current_and_default_text()
    {
        _templates.Saved[MessageTemplateKey.PhoneCode] =
            MessageTemplate.Custom(MessageTemplateKey.PhoneCode, "Código {code}", _admin, _clock.Now);

        var list = await new ListMessageTemplatesUseCase(_templates).ExecuteAsync(default);

        Assert.Equal(["ALERT_CRITICAL", "PHONE_CODE", "COLLABORATOR_INVITATION", "GUEST_LINK_SHARE"], list.Select(t => t.Key));
        var code = list.Single(t => t.Key == "PHONE_CODE");
        Assert.True(code.IsCustom);
        Assert.Equal("Código {code}", code.Body);
        Assert.Contains("{code}", code.DefaultBody);
        Assert.Contains(code.Placeholders, p => p.Token == "{code}" && p.Required);
        Assert.False(list.Single(t => t.Key == "ALERT_CRITICAL").IsCustom);
    }

    [Fact]
    public async Task Saving_stores_the_text_and_saving_the_default_text_goes_back_to_default()
    {
        var useCase = new UpdateMessageTemplateUseCase(_templates, _clock);

        var saved = await useCase.ExecuteAsync(_admin, "alert_critical", "Alerta: {title}", default);
        Assert.True(saved.IsCustom);
        Assert.Equal(_admin, _templates.Saved[MessageTemplateKey.AlertCritical].UpdatedBy);

        var back = await useCase.ExecuteAsync(_admin, "ALERT_CRITICAL", MessageTemplateCatalog.For(MessageTemplateKey.AlertCritical).DefaultBody, default);
        Assert.False(back.IsCustom);
        Assert.Empty(_templates.Saved);
    }

    [Fact]
    public async Task An_invalid_text_or_an_unknown_key_changes_nothing()
    {
        var useCase = new UpdateMessageTemplateUseCase(_templates, _clock);

        await Assert.ThrowsAsync<InvalidMessageTemplateException>(() => useCase.ExecuteAsync(_admin, "PHONE_CODE", "Sin código", default));
        await Assert.ThrowsAsync<MessageTemplateNotFoundException>(() => useCase.ExecuteAsync(_admin, "WELCOME", "Hola", default));
        Assert.Empty(_templates.Saved);
    }

    [Fact]
    public async Task Reset_goes_back_to_the_default_text()
    {
        _templates.Saved[MessageTemplateKey.GuestLinkShare] =
            MessageTemplate.Custom(MessageTemplateKey.GuestLinkShare, "Mira {link}", _admin, _clock.Now);

        var view = await new ResetMessageTemplateUseCase(_templates).ExecuteAsync("GUEST_LINK_SHARE", default);

        Assert.False(view.IsCustom);
        Assert.Empty(_templates.Saved);
    }

    [Fact]
    public async Task Any_user_reads_the_guest_link_text_but_not_the_others()
    {
        _templates.Saved[MessageTemplateKey.GuestLinkShare] =
            MessageTemplate.Custom(MessageTemplateKey.GuestLinkShare, "Mira {place}: {link}", _admin, _clock.Now);
        var useCase = new GetSharedMessageTemplateUseCase(new MessageTexts(_templates));

        Assert.Equal("Mira {place}: {link}", (await useCase.ExecuteAsync("GUEST_LINK_SHARE", default)).Body);
        await Assert.ThrowsAsync<MessageTemplateNotFoundException>(() => useCase.ExecuteAsync("PHONE_CODE", default));
    }
}

public class InvitationNoticeTests
{
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc));
    private readonly FakeNotifications _notifications = new();
    private readonly FakePreferences _prefs = new();
    private readonly FakeOutbox _mail = new();
    private readonly FakeSmsOutbox _sms = new();
    private readonly FakeTemplates _templates = new();
    private readonly Guid _invitee = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _invitation = Guid.NewGuid();
    private readonly Guid _place = Guid.NewGuid();

    private NotificationDispatcher Dispatcher() =>
        new(_notifications, _prefs, _mail, new EmailSettings(true, "http://app"), TestPush.Off(), _clock, _sms, new SmsSettings(true));

    private NotifyCollaboratorInvitationUseCase Invite(bool smsOn = true) =>
        new(Dispatcher(), _prefs, _clock, _sms, new SmsSettings(smsOn), new MessageTexts(_templates));

    private CollaboratorInvitationNotice Notice(Guid? invitee) =>
        new(_invitation, _place, "Casa centro", invitee, "Ana Gómez", ["VIEW_CONSUMPTION", "CONTROL_VALVE"],
            new DateTime(2026, 10, 31, 4, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task A_registered_person_gets_it_in_the_app_and_by_whatsapp_once()
    {
        var first = await Invite().ExecuteAsync(Notice(_invitee), default);
        await Invite().ExecuteAsync(Notice(_invitee), default);   // ms-places retried

        Assert.True(first.InApp);
        Assert.True(first.ShortMessageQueued);
        var n = Assert.Single(_notifications.All);
        Assert.Equal(_invitee, n.Audience.UserId);
        Assert.Equal(NotificationType.CollaboratorInvitation, n.Type);
        Assert.Equal(NotificationSeverity.Warning, n.Severity);
        Assert.Contains("Ana Gómez", n.Body);
        Assert.Contains("Casa centro", n.Body);

        var message = Assert.Single(_sms.All);
        Assert.Equal(_invitee, message.UserId);
        Assert.Contains("ver el consumo de agua y abrir y cerrar la válvula", message.Body);
        Assert.Contains("30 de octubre", message.Body);   // 04:00 UTC = 23:00 of the 30th in Bogotá
    }

    [Fact]
    public async Task The_invitation_is_not_mailed_again_because_ms_iam_already_did()
    {
        _prefs.Saved[(_invitee, NotificationSeverity.Warning)] = NotificationChannels.All & ~NotificationChannels.Sms;

        await Invite().ExecuteAsync(Notice(_invitee), default);

        Assert.Empty(_mail.All);
    }

    [Fact]
    public async Task A_person_without_an_account_only_gets_the_email_of_ms_iam()
    {
        var result = await Invite().ExecuteAsync(Notice(null), default);

        Assert.False(result.InApp);
        Assert.Empty(_notifications.All);
        Assert.Empty(_sms.All);
    }

    [Fact]
    public async Task No_whatsapp_if_the_person_turned_the_channel_off_or_it_is_not_configured()
    {
        _prefs.Saved[(_invitee, NotificationSeverity.Critical)] = NotificationChannels.InApp;
        Assert.False((await Invite().ExecuteAsync(Notice(_invitee), default)).ShortMessageQueued);

        _prefs.Saved.Clear();
        Assert.False((await Invite(smsOn: false).ExecuteAsync(Notice(_invitee), default)).ShortMessageQueued);
        Assert.Empty(_sms.All);
    }

    [Fact]
    public async Task The_whatsapp_uses_the_text_the_admin_saved()
    {
        _templates.Saved[MessageTemplateKey.CollaboratorInvitation] = MessageTemplate.Custom(MessageTemplateKey.CollaboratorInvitation,
            "{inviter} te sumó a {place}", Guid.NewGuid(), _clock.Now);

        await Invite().ExecuteAsync(Notice(_invitee), default);

        Assert.Equal("Ana Gómez te sumó a Casa centro", Assert.Single(_sms.All).Body);
    }

    [Fact]
    public async Task The_owner_learns_the_answer_in_the_app()
    {
        var useCase = new NotifyInvitationAnsweredUseCase(Dispatcher(), _clock);

        await useCase.ExecuteAsync(new InvitationAnsweredNotice(_invitation, _place, "Casa centro", _owner, "Luis", true), default);

        var n = Assert.Single(_notifications.All);
        Assert.Equal(_owner, n.Audience.UserId);
        Assert.Equal(NotificationType.InvitationAnswered, n.Type);
        Assert.Equal("Invitación aceptada", n.Title);
        Assert.Contains("Luis aceptó", n.Body);
        Assert.Empty(_sms.All);
    }

    [Fact]
    public async Task A_notice_without_invitation_or_place_is_refused()
    {
        await Assert.ThrowsAsync<InvalidNotificationException>(() =>
            Invite().ExecuteAsync(Notice(_invitee) with { InvitationId = Guid.Empty }, default));
        await Assert.ThrowsAsync<InvalidNotificationException>(() =>
            new NotifyInvitationAnsweredUseCase(Dispatcher(), _clock).ExecuteAsync(
                new InvitationAnsweredNotice(_invitation, _place, "Casa", Guid.Empty, "Luis", false), default));
    }
}
