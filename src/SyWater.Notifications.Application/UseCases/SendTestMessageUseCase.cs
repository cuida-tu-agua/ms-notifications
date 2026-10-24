using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.Views;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// The admin sends one message of a text to THEIR OWN verified phone, with example data, right now (not through the
/// outbox). It shows whether the provider really works (a Meta token that expired, a number outside the test list...):
/// the answer carries the reason the provider gave instead of failing.
/// </summary>
public sealed class SendTestMessageUseCase(
    IShortMessageSender sender, SmsSettings settings, MessageTexts texts, IUserContactDirectory? contacts = null)
    : ISendTestMessageUseCase
{
    public const string TestPrefix = "(Prueba) ";

    public async Task<TestMessageView> ExecuteAsync(Guid adminId, string key, CancellationToken ct)
    {
        var parsed = MessageTemplateKeys.Parse(key) ?? throw new MessageTemplateNotFoundException(key);
        if (!settings.Enabled || contacts is null) return TestMessageView.Failed("sms_disabled", null);

        UserContact? contact;
        try
        {
            contact = await contacts.FindAsync(adminId, ct);
        }
        catch (ExternalServiceUnavailableException ex)
        {
            return TestMessageView.Failed("unavailable", ex.Message);
        }
        if (contact?.VerifiedPhone is not { } phone) return TestMessageView.Failed("no_verified_phone", null);

        var template = await texts.GetAsync(parsed, ct);
        var body = TestPrefix + template.Render(SampleValues, SmsOutboxItem.MaxBodyLength - TestPrefix.Length);
        var message = parsed == MessageTemplateKey.PhoneCode
            ? new ShortMessage(phone.Phone, phone.Channel, body, ShortMessageKind.VerificationCode, SampleCode)
            : new ShortMessage(phone.Phone, phone.Channel, body);

        var channel = phone.Channel.ToCode();
        var masked = PhoneNumbers.Mask(phone.Phone);
        try
        {
            var id = await sender.SendAsync(message, ct);
            return new TestMessageView(true, channel, masked, id, null, null, body);
        }
        catch (ShortMessageRejectedException ex)
        {
            return new TestMessageView(false, channel, masked, null, "rejected", ex.Message, body);
        }
        catch (ShortMessageSendException ex)
        {
            return new TestMessageView(false, channel, masked, null, "unavailable", ex.Message, body);
        }
        catch (ShortMessagesDisabledException)
        {
            return new TestMessageView(false, channel, masked, null, "sms_disabled", null, body);
        }
    }

    public const string SampleCode = "123456";

    private static readonly IReadOnlyDictionary<string, string?> SampleValues = new Dictionary<string, string?>
    {
        ["title"] = "Válvula cerrada",
        ["place"] = "Casa de prueba",
        ["code"] = SampleCode,
        ["minutes"] = "5",
        ["inviter"] = "Ana Gómez",
        ["permissions"] = "ver el consumo de agua y recibir las alertas",
        ["expires"] = "31 de octubre",
        ["owner"] = "Ana Gómez",
        ["link"] = "https://cuidatuagua.app/guest/prueba",
    };
}

/// <summary>Which provider each mode uses right now (the admin sees whether WhatsApp is real or simulated).</summary>
public sealed class GetShortMessageProvidersUseCase(ShortMessageProviders providers) : IGetShortMessageProvidersUseCase
{
    public ShortMessageProvidersView Execute() => new(providers.Sms, providers.WhatsApp, providers.WhatsAppTemplate);
}

/// <param name="Sms">twilio · mailpit · "" (off).</param>
/// <param name="WhatsApp">meta · twilio · mailpit · "" (off).</param>
/// <param name="WhatsAppTemplate">Meta sends with an approved template (arrives at any time).</param>
public sealed record ShortMessageProviders(string Sms, string WhatsApp, bool WhatsAppTemplate);
