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
}

public sealed record NewMailNotification(Guid MailAccountId, Guid MailId, Guid FolderId, string Sender, string Subject);

public interface IPushNotificationService
{
    Task NotifyNewMailAsync(NewMailNotification notification, CancellationToken cancellationToken);
}

public interface IFileStorage
{
    Task<StoredFile> SaveAsync(Guid accountId, Guid mailId, Guid attachmentId, Func<Stream, CancellationToken, Task> write, long maxBytes, CancellationToken cancellationToken);
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken);
}

public sealed record StoredFile(string RelativePath, long SizeBytes);

public sealed class AttachmentLimitExceededException() : Exception("Attachment exceeds the size limit.");
