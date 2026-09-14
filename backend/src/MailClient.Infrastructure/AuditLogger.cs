using System.Text.Json;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;

namespace MailClient.Infrastructure.Observability;

public sealed class AuditLogger(AppDbContext db)
{
    public async Task WriteAsync(Guid? accountId, string action, string entityType, string? entityId, object? metadata, string? correlationId, CancellationToken cancellationToken)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            TimestampUtc = DateTime.UtcNow,
            CorrelationId = correlationId,
            Metadata = metadata is null ? null : LogRedactor.Redact(JsonSerializer.Serialize(metadata))
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
