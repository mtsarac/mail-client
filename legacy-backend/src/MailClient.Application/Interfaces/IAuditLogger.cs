using System.Text.Json;

// Audit seam: semantic user-action records persisted in PostgreSQL.
// Metadata must be small, safe key/value pairs. Never pass secrets.
namespace MailClient.Application.Interfaces;

public interface IAuditLogger
{
    Task LogAsync(
        Guid? userId,
        string action,
        string entityType,
        string? entityId,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken cancellationToken = default);
}

public static class AuditEntities
{
    public const string User = "user";
    public const string MailAccount = "mail-account";
    public const string MailFolder = "mail-folder";
    public const string Mail = "mail";
    public const string Device = "device";
}
