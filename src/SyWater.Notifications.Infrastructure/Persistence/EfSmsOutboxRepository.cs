using Microsoft.EntityFrameworkCore;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.ShortMessages;
using SyWater.Notifications.Infrastructure.Persistence.Entities;

namespace SyWater.Notifications.Infrastructure.Persistence;

public sealed class EfSmsOutboxRepository(NotificationDbContext db) : ISmsOutboxRepository
{
    public async Task<bool> AddAsync(SmsOutboxItem item, CancellationToken ct)
    {
        if (await ExistsAsync(item, ct)) return false;

        db.SmsOutbox.Add(new SmsOutboxEntity
        {
            Id = item.Id,
            UserId = item.UserId,
            SourceEventId = item.SourceEventId,
            Body = item.Body,
            Status = ToDb(item.Status),
            Channel = item.Channel?.ToCode(),
            ProviderMessageId = item.ProviderMessageId,
            Attempts = item.Attempts,
            NextAttemptAt = item.NextAttemptAt,
            LastError = item.LastError,
            CreatedAt = item.CreatedAt,
            SentAt = item.SentAt,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            // UX_sout_source_user: the same event was processed a moment ago by another instance
            if (await ExistsAsync(item, ct)) return false;
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private Task<bool> ExistsAsync(SmsOutboxItem item, CancellationToken ct) =>
        item.SourceEventId is null
            ? Task.FromResult(false)
            : db.SmsOutbox.AnyAsync(e => e.SourceEventId == item.SourceEventId && e.UserId == item.UserId, ct);

    public async Task<IReadOnlyList<SmsOutboxItem>> GetDueAsync(DateTime now, int max, CancellationToken ct)
    {
        var rows = await db.SmsOutbox.AsNoTracking()
            .Where(e => e.Status == "PENDING" && e.NextAttemptAt <= now)
            .OrderBy(e => e.NextAttemptAt).Take(max).ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<bool> SaveOutcomeAsync(SmsOutboxItem item, CancellationToken ct) =>
        await db.SmsOutbox.Where(e => e.Id == item.Id && e.Status == "PENDING")
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ToDb(item.Status))
                .SetProperty(e => e.Channel, item.Channel == null ? null : item.Channel.Value.ToCode())
                .SetProperty(e => e.ProviderMessageId, item.ProviderMessageId)
                .SetProperty(e => e.Attempts, item.Attempts)
                .SetProperty(e => e.NextAttemptAt, item.NextAttemptAt)
                .SetProperty(e => e.LastError, item.LastError)
                .SetProperty(e => e.SentAt, item.SentAt), ct) > 0;

    private static SmsOutboxItem ToDomain(SmsOutboxEntity e) => SmsOutboxItem.Restore(
        e.Id, e.UserId, e.SourceEventId, e.Body, StatusFromDb(e.Status), ShortMessageChannels.Parse(e.Channel),
        e.ProviderMessageId, e.Attempts, NotificationMapper.Utc(e.NextAttemptAt), e.LastError, NotificationMapper.Utc(e.CreatedAt),
        e.SentAt is null ? null : NotificationMapper.Utc(e.SentAt.Value));

    private static string ToDb(SmsStatus s) => s switch
    {
        SmsStatus.Sent => "SENT",
        SmsStatus.Failed => "FAILED",
        SmsStatus.Skipped => "SKIPPED",
        _ => "PENDING",
    };

    private static SmsStatus StatusFromDb(string s) => s switch
    {
        "SENT" => SmsStatus.Sent,
        "FAILED" => SmsStatus.Failed,
        "SKIPPED" => SmsStatus.Skipped,
        _ => SmsStatus.Pending,
    };
}
