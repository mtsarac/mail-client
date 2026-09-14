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

public sealed record SendMailCommand(Guid AccountId, string ToAddress, string Subject, string? BodyHtml, string? BodyText, IReadOnlyList<SendMailAttachment> Attachments)
{
    public string IdempotencyKey { get; init; } = "";
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
