using System.Text.RegularExpressions;
using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// HU-028: sends the short messages of the outbox whose time came. The phone and its mode are asked to ms-iam right now
/// (the user may have changed them since the alert); without a verified phone the message is SKIPPED. A failure never
/// stops the others: it is recorded on its own message and retried later, until the attempts run out.
/// </summary>
public sealed class SendDueShortMessagesUseCase(
    ISmsOutboxRepository outbox, IUserContactDirectory contacts, IShortMessageSender sender, SmsRetryPolicy policy, TimeProvider clock)
    : ISendDueShortMessagesUseCase
{
    public const int BatchSize = 20;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var sent = 0;

        foreach (var item in await outbox.GetDueAsync(now, BatchSize, ct))
        {
            ShortMessageChannel? channel = null;
            try
            {
                var contact = await contacts.FindAsync(item.UserId, ct);
                if (contact?.VerifiedPhone is not { } phone)
                {
                    item.Skip("The user has no verified phone.");
                }
                else
                {
                    channel = phone.Channel;
                    var providerId = await sender.SendAsync(new ShortMessage(phone.Phone, phone.Channel, item.Body), ct);
                    item.MarkSent(phone.Channel, providerId, now);
                    sent++;
                }
            }
            catch (ShortMessageRejectedException ex)
            {
                item.MarkRejected(channel ?? ShortMessageChannel.Sms, ex.Message);
            }
            catch (Exception ex) when (ex is ShortMessageSendException or ExternalServiceUnavailableException)
            {
                item.RegisterFailure(ex.Message, now, policy);
            }

            await outbox.SaveOutcomeAsync(item, ct);
        }

        return sent;
    }
}

/// <summary>
/// HU-028: delivers the code that verifies a phone by the mode the user picked, so a number that does not work for that
/// mode can never be verified with it. ms-iam generates, stores and checks the code; this service only delivers it.
/// </summary>
public sealed partial class SendVerificationCodeUseCase(IShortMessageSender sender, SmsSettings settings, MessageTexts? texts = null)
    : ISendVerificationCodeUseCase
{
    [GeneratedRegex(@"^[0-9]{6}$")]
    private static partial Regex SixDigits();

    public async Task<string?> ExecuteAsync(string toPhone, string? channel, string code, int validMinutes, CancellationToken ct)
    {
        if (!PhoneNumbers.IsE164(toPhone)) throw new InvalidShortMessageException("The phone must be in E.164 format, e.g. +573001112233.");
        if (ShortMessageChannels.Parse(channel) is not { } mode) throw new InvalidShortMessageException("The mode must be SMS or WHATSAPP.");
        if (code is null || !SixDigits().IsMatch(code)) throw new InvalidShortMessageException("The code must have 6 digits.");
        if (validMinutes is < 1 or > 60) throw new InvalidShortMessageException("The code must be valid between 1 and 60 minutes.");
        if (!settings.Enabled) throw new ShortMessagesDisabledException();

        var template = await (texts ?? MessageTexts.Defaults).GetAsync(MessageTemplateKey.PhoneCode, ct);
        return await sender.SendAsync(
            new ShortMessage(toPhone, mode, ShortMessageComposer.ForVerificationCode(code, validMinutes, template), ShortMessageKind.VerificationCode, code), ct);
    }
}
