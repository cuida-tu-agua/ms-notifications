using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Messages;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// The text each WhatsApp / SMS is written with right now: the one the admin saved, or the default one. Read on every
/// message, so an edit applies to the next alert without restarting anything.
/// </summary>
public sealed class MessageTexts(IMessageTemplateRepository? templates = null)
{
    /// <summary>Only the default texts (tests and environments without the table).</summary>
    public static MessageTexts Defaults { get; } = new();

    public async Task<MessageTemplate> GetAsync(MessageTemplateKey key, CancellationToken ct)
    {
        if (templates is null) return MessageTemplate.Default(key);
        try
        {
            return await templates.FindCustomAsync(key, ct) ?? MessageTemplate.Default(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A critical alert must never be lost because its custom text could not be read: it leaves with the default one
            return MessageTemplate.Default(key);
        }
    }
}
