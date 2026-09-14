// Semantic user-action record persisted in PostgreSQL. Never stores secrets.
namespace MailClient.Domain.Entities;

public class AuditLog
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? Metadata { get; set; }
    public string? CorrelationId { get; set; }
    public User? User { get; set; }
}
