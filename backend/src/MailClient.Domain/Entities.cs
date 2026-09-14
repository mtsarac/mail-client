using MailClient.Domain.Enums;

namespace MailClient.Domain.Entities;

public sealed class MailAccount
{
    public Guid Id { get; set; }
    public string EmailAddress { get; set; } = "";
    public string NormalizedEmailAddress { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Username { get; set; } = "";
    public MailProvider Provider { get; set; }
    public AuthenticationMethod AuthenticationMethod { get; set; }
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; }
    public MailSecurity ImapSecurity { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; }
    public MailSecurity SmtpSecurity { get; set; }
    public DiscoverySource DiscoverySource { get; set; }
    public bool SaveSentCopy { get; set; } = true;
    public MailAccountStatus Status { get; set; } = MailAccountStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastAuthenticatedAt { get; set; }
    public ICollection<MailCredential> Credentials { get; set; } = [];
    public ICollection<MailSession> Sessions { get; set; } = [];
}

public sealed class MailCredential
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public AuthenticationMethod AuthenticationMethod { get; set; }
    public MailProvider Provider { get; set; }
    public string EncryptedMaterial { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
    public string? Scopes { get; set; }
    public string? ProviderMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MailAccount MailAccount { get; set; } = null!;
}

public sealed class MailSession
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public string RefreshTokenHash { get; set; } = "";
    public string? DeviceIdentifier { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedBySessionId { get; set; }
    public MailAccount MailAccount { get; set; } = null!;
}

public sealed class MailFolder { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public string Name { get; set; } = ""; public string FullName { get; set; } = ""; public uint UidValidity { get; set; } public bool IsSyncEnabled { get; set; } = true; }
public sealed class Mail { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public Guid MailFolderId { get; set; } public uint Uid { get; set; } public string Subject { get; set; } = ""; public string FromAddress { get; set; } = ""; public string BodyText { get; set; } = ""; public string BodyHtml { get; set; } = ""; public bool IsRead { get; set; } public DateTime ReceivedAt { get; set; } }
public sealed class Attachment { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public Guid MailId { get; set; } public string FileName { get; set; } = ""; public string ContentType { get; set; } = ""; public string StoragePath { get; set; } = ""; public long SizeBytes { get; set; } }
public sealed class SyncState { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public Guid MailFolderId { get; set; } public uint UidValidity { get; set; } public uint LastUid { get; set; } public long NextUidScanStart { get; set; } = 1; }
public sealed class SyncSkippedUid { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public Guid MailFolderId { get; set; } public uint Uid { get; set; } public string Reason { get; set; } = ""; public DateTime SkippedAt { get; set; } }
public sealed class SendOperation { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public string IdempotencyKey { get; set; } = ""; public string Fingerprint { get; set; } = ""; public SendOperationStatus Status { get; set; } public DateTime CreatedAt { get; set; } public DateTime UpdatedAt { get; set; } }
public sealed class DeviceToken { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public Guid? MailSessionId { get; set; } public string Token { get; set; } = ""; public string Platform { get; set; } = ""; public DateTime RegisteredAt { get; set; } public DateTime? LastSeenAt { get; set; } }
public sealed class AuditLog { public Guid Id { get; set; } public Guid? MailAccountId { get; set; } public string Action { get; set; } = ""; public string EntityType { get; set; } = ""; public string? EntityId { get; set; } public DateTime TimestampUtc { get; set; } public string? CorrelationId { get; set; } public string? Metadata { get; set; } }
