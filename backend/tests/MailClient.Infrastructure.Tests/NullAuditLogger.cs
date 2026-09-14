using MailClient.Application.Interfaces;

namespace MailClient.Infrastructure.Tests;

public sealed class NullAuditLogger : IAuditLogger
{
    public static readonly NullAuditLogger Instance = new();

    public Task LogAsync(
        Guid? userId,
        string action,
        string entityType,
        string? entityId,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
