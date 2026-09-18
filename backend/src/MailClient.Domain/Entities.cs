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
    /// <summary>Set when the account is disabled because its email left the production allowlist; cleared if it is re-added. Drives grace-period deletion.</summary>
    public DateTime? AccessRevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastAuthenticatedAt { get; set; }
    public ICollection<MailCredential> Credentials { get; set; } = [];
    public ICollection<MailSession> Sessions { get; set; } = [];
    public ICollection<MailFolder> Folders { get; set; } = [];
    public ICollection<Mail> Mails { get; set; } = [];
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

public sealed class MailFolder
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public MailFolderType FolderType { get; set; } = MailFolderType.Unknown;
    public uint UidValidity { get; set; }
    public bool IsSyncEnabled { get; set; } = true;
    public bool IsAvailable { get; set; } = true;
    public MailAccount? MailAccount { get; set; }
    public SyncState? SyncState { get; set; }
    public ICollection<Mail> Mails { get; set; } = [];
}
public sealed class Mail
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public Guid MailFolderId { get; set; }
    public Guid? PreviousMailFolderId { get; set; }
    public Guid? ExpectedMailFolderId { get; set; }
    public MailReconciliationState ReconciliationState { get; set; }
    public bool IsRestoreReconciliation { get; set; }
    public Guid? ConversationId { get; set; }
    public uint Uid { get; set; }
    public uint UidValidity { get; set; }
    public string MessageId { get; set; } = "";
    public string InReplyToMessageId { get; set; } = "";
    public string References { get; set; } = "";
    public string Subject { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromDisplayName { get; set; } = "";
    public string ToAddress { get; set; } = "";
    public string BodyText { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public bool IsRead { get; set; }
    public bool Answered { get; set; }
    public bool Flagged { get; set; }
    public bool Draft { get; set; }
    public bool Deleted { get; set; }
    public bool Recent { get; set; }
    public bool HasAttachments { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime InternalDate { get; set; }
    public MailAccount? MailAccount { get; set; }
    public MailFolder? MailFolder { get; set; }
    public ICollection<Attachment> Attachments { get; set; } = [];
    public ICollection<MailParticipant> Participants { get; set; } = [];
    public ICollection<MailHeader> Headers { get; set; } = [];
    public Conversation? Conversation { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public string NormalizedSubject { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    public MailAccount? MailAccount { get; set; }
    public ICollection<Mail> Mails { get; set; } = [];
}

public sealed class MailParticipant
{
    public Guid Id { get; set; }
    public Guid MailId { get; set; }
    public ParticipantType Type { get; set; }
    public string Address { get; set; } = "";
    public string NormalizedAddress { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int SortOrder { get; set; }
}

public sealed class MailHeader
{
    public Guid Id { get; set; }
    public Guid MailId { get; set; }
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class Attachment
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public Guid MailId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsInline { get; set; }
    public string ContentId { get; set; } = "";
    public string ContentDisposition { get; set; } = "";
    public Mail? Mail { get; set; }
}
public sealed class SyncState
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public Guid MailFolderId { get; set; }
    public uint UidValidity { get; set; }
    public uint LastUid { get; set; }
    public long NextUidScanStart { get; set; } = 1;

    /// <summary>Exclusive upper bound for the next newest-first backfill page; 0 when history is complete.</summary>
    public long BackfillNextUid { get; set; }

    /// <summary>Resume point of the current chunked flag/removal reconciliation pass; 0 starts a new pass.</summary>
    public uint FlagScanCursorUid { get; set; }
    public DateTime? LastNewMailSyncAt { get; set; }
    public DateTime? LastFlagSyncAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public SyncFailureCategory? LastFailureCategory { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
    public DateTime? LastErrorNotifiedAt { get; set; }
    public MailFolder? MailFolder { get; set; }
}
public sealed class SyncSkippedUid { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public Guid MailFolderId { get; set; } public uint Uid { get; set; } public string Reason { get; set; } = ""; public DateTime SkippedAt { get; set; } }
public sealed class SendOperation
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public SendOperationStatus Status { get; set; } = SendOperationStatus.InProgress;
    public bool SentCopySaved { get; set; }
    public string? Warning { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
public sealed class DeviceToken { public Guid Id { get; set; } public Guid MailAccountId { get; set; } public Guid? MailSessionId { get; set; } public string Token { get; set; } = ""; public string Platform { get; set; } = ""; public string? AppVersion { get; set; } public string? Locale { get; set; } public DateTime RegisteredAt { get; set; } public DateTime? LastSeenAt { get; set; } }
public sealed class AuditLog { public Guid Id { get; set; } public Guid? MailAccountId { get; set; } public string Action { get; set; } = ""; public string EntityType { get; set; } = ""; public string? EntityId { get; set; } public DateTime TimestampUtc { get; set; } public string? CorrelationId { get; set; } public string? Metadata { get; set; } }
