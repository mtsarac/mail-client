using MailClient.Domain.Enums;

namespace MailClient.Application.Mail;

public enum MailConnectionFailure { Network, Authentication, Tls, Protocol }

public sealed record MailServerEndpoint(string Host, int Port, MailSecurity Security);

public sealed class MailConnectionException(MailConnectionFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public MailConnectionFailure Failure { get; } = failure;
    public string Operation { get; init; } = "";
}

public sealed class SmtpDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record SendMailAttachment(string FileName, string ContentType, Stream Content);

public sealed record SendMailCommand(
    Guid AccountId,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string? BodyHtml,
    string? BodyText,
    IReadOnlyList<SendMailAttachment> Attachments,
    Guid? ReplySourceMailId = null)
{
    public SendMailCommand(Guid accountId, string toAddress, string subject, string? bodyHtml, string? bodyText, IReadOnlyList<SendMailAttachment> attachments)
        : this(accountId, [toAddress], [], [], subject, bodyHtml, bodyText, attachments)
    {
    }

    public string IdempotencyKey { get; init; } = "";
    public string? TrustedMessageId { get; init; }
    public string? TrustedInReplyToMessageId { get; init; }
    public string? TrustedReferences { get; init; }
    public Guid? IdentityId { get; init; }
    public bool RequestReadReceipt { get; init; }
    public bool RequestDeliveryReceipt { get; init; }
}

public enum PushEventType
{
    NewMail,
    MailStateChanged,
    AccountReauthenticationRequired,
    /// <summary>Contract-reserved. Not emitted yet: nothing can reliably distinguish
    /// persistent sync failure from transient errors before Phase 9 retry classification.</summary>
    SyncError,
    SnoozeExpired,
    ReplyReminder
}

/// <summary>
/// Typed push event. Payloads stay minimal: identifiers the client uses to fetch
/// authoritative state from the API, plus sender/subject/short text preview only as the
/// account's notification privacy allows. Never attach full mail bodies, HTML, credentials,
/// tokens, attachment contents, or raw exception messages.
/// </summary>
public sealed record PushEvent(
    PushEventType Type,
    Guid MailAccountId,
    Guid? MailId = null,
    Guid? ConversationId = null,
    Guid? FolderId = null,
    string? Operation = null,
    string? SenderPreview = null,
    string? SubjectPreview = null,
    string? BodyPreview = null,
    string? RecipientPreview = null);

/// <summary>
/// Best-effort push delivery. Implementations must never let a push failure fail
/// the mail/sync operation that triggered the event.
/// </summary>
public interface IPushNotificationService
{
    Task NotifyAsync(PushEvent pushEvent, CancellationToken cancellationToken);
}

public interface IFileStorage
{
    Task<StoredFile> SaveAsync(Guid accountId, Guid mailId, Guid attachmentId, Func<Stream, CancellationToken, Task> write, long maxBytes, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken);
    Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Cheap readiness probe for the configured backend; used by /health/ready.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

public sealed record StoredFile(string RelativePath, long SizeBytes);

public sealed class AttachmentLimitExceededException() : Exception("Attachment exceeds the size limit.");
