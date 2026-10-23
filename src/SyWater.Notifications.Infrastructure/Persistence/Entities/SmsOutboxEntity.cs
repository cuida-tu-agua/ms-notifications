using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SyWater.Notifications.Infrastructure.Persistence.Entities;

/// <summary>1:1 copy of notification.sms_outbox (release v1.6 of ms-notification-db).</summary>
[Table("sms_outbox", Schema = "notification")]
public sealed class SmsOutboxEntity
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.None)] public Guid Id { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("source_event_id")] public string? SourceEventId { get; set; }
    [Column("body")] public string Body { get; set; } = "";
    [Column("status")] public string Status { get; set; } = "PENDING";
    [Column("channel")] public string? Channel { get; set; }
    [Column("provider_message_id")] public string? ProviderMessageId { get; set; }
    [Column("attempts")] public int Attempts { get; set; }
    [Column("next_attempt_at")] public DateTime NextAttemptAt { get; set; }
    [Column("last_error")] public string? LastError { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("sent_at")] public DateTime? SentAt { get; set; }
}
