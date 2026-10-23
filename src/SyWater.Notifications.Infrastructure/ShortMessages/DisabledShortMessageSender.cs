using SyWater.Notifications.Application.Ports.Out;

namespace SyWater.Notifications.Infrastructure.ShortMessages;

/// <summary>No Sms:Provider configured: the channel is off. Nothing is queued, and a verification code answers 503 sms.disabled.</summary>
public sealed class DisabledShortMessageSender : IShortMessageSender
{
    public Task<string?> SendAsync(ShortMessage message, CancellationToken ct) => throw new ShortMessagesDisabledException();
}
