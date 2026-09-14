using System.Text.Json;
using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Audit;

public sealed class EfAuditLogger(
    AppDbContext db,
    ILogger<EfAuditLogger> logger) : IAuditLogger
{
    public async Task LogAsync(
        Guid? userId,
        string action,
        string entityType,
        string? entityId,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var correlationId = CorrelationContext.Current;
        string? metadataJson = null;
        if (metadata is { Count: > 0 })
        {
            try
            {
                metadataJson = JsonSerializer.Serialize(metadata);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Audit metadata for {Action} could not be serialized.", action);
            }
        }

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            TimestampUtc = DateTime.UtcNow,
            Metadata = metadataJson,
            CorrelationId = correlationId
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
