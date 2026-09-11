using MailClient.Domain.Enums;

// Idempotent send-operation record keyed by user and idempotency key.
namespace MailClient.Domain.Entities;

public class SendOperation
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public SendOperationStatus Status { get; set; } = SendOperationStatus.InProgress;
    public bool SentCopySaved { get; set; }
    public string? Warning { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public User? User { get; set; }
}
