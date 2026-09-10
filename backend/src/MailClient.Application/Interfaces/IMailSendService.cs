using MailClient.Application;

namespace MailClient.Application.Interfaces;

public interface IMailSendService
{
    Task<ServiceResult<SendMailResult>> SendAsync(
        Guid userId,
        SendMailCommand command,
        CancellationToken cancellationToken);
}

public sealed record SendMailCommand(
    Guid AccountId,
    string ToAddress,
    string Subject,
    string? BodyHtml,
    string? BodyText,
    IReadOnlyList<SendMailAttachment> Attachments,
    string? IdempotencyKey = null);

public sealed record SendMailAttachment(
    string FileName,
    string ContentType,
    Stream Content);

public sealed record SendMailResult(
    bool Sent,
    bool SentCopySaved,
    string? Warning);
