using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.Tests;

public class SendTestMessageTests
{
    private readonly FakeShortMessageSender _sender = new();
    private readonly FakeContacts _contacts = new();
    private readonly FakeTemplates _templates = new();
    private readonly Guid _admin = Guid.NewGuid();

    private SendTestMessageUseCase UseCase(bool on = true, bool withContacts = true) =>
        new(_sender, new SmsSettings(on), new MessageTexts(_templates), withContacts ? _contacts : null);

    private void AdminWithPhone(ShortMessageChannel channel = ShortMessageChannel.WhatsApp) =>
        _contacts.Known[_admin] = new UserContact(_admin, "admin@mail.com", "Ada Admin", "+573001112233", true, channel);

    [Fact]
    public async Task The_admin_gets_the_text_with_example_data_at_once_by_the_mode_of_their_phone()
    {
        AdminWithPhone();
        _templates.Saved[MessageTemplateKey.AlertCritical] =
            MessageTemplate.Custom(MessageTemplateKey.AlertCritical, "URGENTE {title} en {place}", _admin, DateTime.UtcNow);

        var result = await UseCase().ExecuteAsync(_admin, "ALERT_CRITICAL", default);

        Assert.True(result.Sent);
        Assert.Equal("WHATSAPP", result.Channel);
        Assert.Equal("+57300***233", result.MaskedPhone);
        Assert.Equal("SM1", result.ProviderMessageId);
        var sent = Assert.Single(_sender.Sent);
        Assert.Equal("(Prueba) URGENTE Válvula cerrada en Casa de prueba", sent.Body);
        Assert.Equal(sent.Body, result.Body);
    }

    [Fact]
    public async Task The_phone_code_goes_as_a_code_so_Meta_can_use_the_authentication_template()
    {
        AdminWithPhone(ShortMessageChannel.Sms);

        await UseCase().ExecuteAsync(_admin, "PHONE_CODE", default);

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(ShortMessageChannel.Sms, sent.Channel);
        Assert.Equal(ShortMessageKind.VerificationCode, sent.Kind);
        Assert.Equal("123456", sent.Code);
    }

    [Fact]
    public async Task What_the_provider_refuses_comes_back_with_its_reason_instead_of_failing()
    {
        AdminWithPhone();
        _sender.RejectWith = "Meta 400 (131030): Recipient phone number not in allowed list";

        var result = await UseCase().ExecuteAsync(_admin, "GUEST_LINK_SHARE", default);

        Assert.False(result.Sent);
        Assert.Equal("rejected", result.Problem);
        Assert.Contains("131030", result.Detail);
    }

    [Fact]
    public async Task A_provider_that_is_down_or_an_expired_token_is_reported_as_unavailable()
    {
        AdminWithPhone();
        _sender.Down = true;

        var result = await UseCase().ExecuteAsync(_admin, "COLLABORATOR_INVITATION", default);

        Assert.Equal("unavailable", result.Problem);
    }

    [Fact]
    public async Task Without_a_verified_phone_or_with_the_channel_off_nothing_is_sent()
    {
        Assert.Equal("no_verified_phone", (await UseCase().ExecuteAsync(_admin, "ALERT_CRITICAL", default)).Problem);
        AdminWithPhone();
        Assert.Equal("sms_disabled", (await UseCase(on: false).ExecuteAsync(_admin, "ALERT_CRITICAL", default)).Problem);
        Assert.Equal("sms_disabled", (await UseCase(withContacts: false).ExecuteAsync(_admin, "ALERT_CRITICAL", default)).Problem);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task An_unknown_text_is_404()
    {
        await Assert.ThrowsAsync<MessageTemplateNotFoundException>(() => UseCase().ExecuteAsync(_admin, "WELCOME", default));
    }

    [Fact]
    public void The_providers_say_whether_WhatsApp_is_real()
    {
        var view = new GetShortMessageProvidersUseCase(new ShortMessageProviders("mailpit", "meta", true)).Execute();

        Assert.Equal("mailpit", view.Sms);
        Assert.Equal("meta", view.WhatsApp);
        Assert.True(view.WhatsAppTemplate);
    }
}
