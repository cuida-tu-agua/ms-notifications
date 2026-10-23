using Microsoft.EntityFrameworkCore;
using SyWater.Notifications.Infrastructure.Persistence.Entities;

namespace SyWater.Notifications.Infrastructure.Persistence;

/// <summary>EF Core session against sy-water-db. The schema is owned by ms-notification-db (Liquibase): no EF migrations.</summary>
public class NotificationDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<NotificationReadEntity> Reads => Set<NotificationReadEntity>();
    public DbSet<PlaceDeviceEntity> PlaceDevices => Set<PlaceDeviceEntity>();
    public DbSet<NotificationPreferenceEntity> Preferences => Set<NotificationPreferenceEntity>();
    public DbSet<EmailOutboxEntity> EmailOutbox => Set<EmailOutboxEntity>();
    public DbSet<PushTokenEntity> PushTokens => Set<PushTokenEntity>();
    public DbSet<SmsOutboxEntity> SmsOutbox => Set<SmsOutboxEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationReadEntity>().HasKey(r => new { r.NotificationId, r.UserId });   // PK_notification_reads
        modelBuilder.Entity<PlaceDeviceEntity>().HasIndex(d => d.DeviceId).IsUnique();   // UQ_pdev_device
        modelBuilder.Entity<NotificationPreferenceEntity>().HasKey(p => new { p.UserId, p.Severity });   // PK_notification_preferences
        modelBuilder.Entity<EmailOutboxEntity>().HasIndex(e => new { e.SourceEventId, e.UserId }).IsUnique().HasDatabaseName("UX_eout_source_user");
        modelBuilder.Entity<PushTokenEntity>().HasIndex(t => t.Token).IsUnique().HasDatabaseName("UQ_ptok_token");
        modelBuilder.Entity<SmsOutboxEntity>().HasIndex(e => new { e.SourceEventId, e.UserId }).IsUnique().HasDatabaseName("UX_sout_source_user");

        // UX_notif_source_audience (SQL Server treats NULLs as equal in unique indexes; SQLite does not,
        // so the repository also checks before inserting)
        modelBuilder.Entity<NotificationEntity>()
            .HasIndex(n => new { n.SourceEventId, n.UserId, n.Role })
            .IsUnique().HasDatabaseName("UX_notif_source_audience");
    }
}
