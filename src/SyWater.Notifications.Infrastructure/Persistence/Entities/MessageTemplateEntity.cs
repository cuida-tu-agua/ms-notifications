using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SyWater.Notifications.Infrastructure.Persistence.Entities;

/// <summary>1:1 copy of notification.message_templates (release v1.7 of ms-notification-db).</summary>
[Table("message_templates", Schema = "notification")]
public sealed class MessageTemplateEntity
{
    [Key, Column("template_key")] public string TemplateKey { get; set; } = "";
    [Column("body")] public string Body { get; set; } = "";
    [Column("updated_by")] public Guid? UpdatedBy { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}
