using SyWater.Notifications.Domain.Messages;

namespace SyWater.Notifications.Application.Ports.Out;

/// <summary>The texts an admin customized. A key that is not stored uses its default text.</summary>
public interface IMessageTemplateRepository
{
    /// <summary>The saved text of that message, or null if it still uses the default one.</summary>
    Task<MessageTemplate?> FindCustomAsync(MessageTemplateKey key, CancellationToken ct);

    Task<IReadOnlyList<MessageTemplate>> ListCustomAsync(CancellationToken ct);

    /// <summary>Inserts or replaces the text of that message.</summary>
    Task SaveAsync(MessageTemplate template, CancellationToken ct);

    /// <summary>Back to the default text. False if it was already the default one.</summary>
    Task<bool> DeleteAsync(MessageTemplateKey key, CancellationToken ct);
}
