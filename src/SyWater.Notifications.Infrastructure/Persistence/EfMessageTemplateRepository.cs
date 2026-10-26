using Microsoft.EntityFrameworkCore;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Infrastructure.Persistence.Entities;

namespace SyWater.Notifications.Infrastructure.Persistence;

public sealed class EfMessageTemplateRepository(NotificationDbContext db) : IMessageTemplateRepository
{
    public async Task<MessageTemplate?> FindCustomAsync(MessageTemplateKey key, CancellationToken ct)
    {
        var code = key.ToCode();
        var row = await db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.TemplateKey == code, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<MessageTemplate>> ListCustomAsync(CancellationToken ct) =>
        (await db.MessageTemplates.AsNoTracking().ToListAsync(ct))
        .Select(ToDomain)
        .OfType<MessageTemplate>()
        .ToList();

    public async Task SaveAsync(MessageTemplate template, CancellationToken ct)
    {
        var code = template.Key.ToCode();
        try
        {
            var row = await db.MessageTemplates.FirstOrDefaultAsync(t => t.TemplateKey == code, ct);
            if (row is null)
            {
                row = new MessageTemplateEntity { TemplateKey = code };
                db.MessageTemplates.Add(row);
            }
            row.Body = template.Body;
            row.UpdatedBy = template.UpdatedBy;
            row.UpdatedAt = template.UpdatedAt ?? DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // PK_message_templates: two admins saved the same message at once → the last one wins
            db.ChangeTracker.Clear();
            await db.MessageTemplates.Where(t => t.TemplateKey == code).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Body, template.Body)
                .SetProperty(t => t.UpdatedBy, template.UpdatedBy)
                .SetProperty(t => t.UpdatedAt, template.UpdatedAt ?? DateTime.UtcNow), ct);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<bool> DeleteAsync(MessageTemplateKey key, CancellationToken ct)
    {
        var code = key.ToCode();
        return await db.MessageTemplates.Where(t => t.TemplateKey == code).ExecuteDeleteAsync(ct) > 0;
    }

    // A key the code no longer knows is ignored (its row is harmless)
    private static MessageTemplate? ToDomain(MessageTemplateEntity row) =>
        MessageTemplateKeys.Parse(row.TemplateKey) is { } key
            ? MessageTemplate.Restore(key, row.Body, row.UpdatedBy, NotificationMapper.Utc(row.UpdatedAt))
            : null;
}
