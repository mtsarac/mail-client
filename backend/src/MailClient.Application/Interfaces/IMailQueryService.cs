using MailClient.Application;
using MailClient.Domain.Enums;

namespace MailClient.Application.Interfaces;

public interface IMailQueryService
{
    Task<ServiceResult<MailPageDto>> ListAsync(
        Guid userId,
        MailListQuery query,
        CancellationToken cancellationToken);

    Task<ServiceResult<MailDetailDto>> GetDetailAsync(
        Guid userId,
        Guid mailId,
        CancellationToken cancellationToken);

    Task<ServiceResult<AttachmentFile>> GetAttachmentAsync(
        Guid userId,
        Guid mailId,
        Guid attachmentId,
        CancellationToken cancellationToken);
}

public sealed record MailListQuery(
    Guid? AccountId,
    Guid? FolderId,
    MailFolderType? FolderType,
    int Page,
    int PageSize);

public sealed record MailPageDto(
    IReadOnlyList<MailSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record MailSummaryDto(
    Guid Id,
    Guid MailAccountId,
    string MailAccountEmail,
    Guid FolderId,
    MailFolderType FolderType,
    string FromDisplayName,
    string FromAddress,
    string Subject,
    DateTime ReceivedAt,
    bool IsRead,
    bool HasAttachments);

public sealed record MailDetailDto(
    Guid Id,
    Guid MailAccountId,
    string MailAccountEmail,
    Guid FolderId,
    MailFolderType FolderType,
    string MessageId,
    string Subject,
    string FromAddress,
    string FromDisplayName,
    string ToAddress,
    string BodyHtml,
    string BodyText,
    DateTime ReceivedAt,
    bool IsRead,
    bool HasAttachments,
    IReadOnlyList<AttachmentDto> Attachments);

public sealed record AttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    bool IsInline,
    string ContentId);

public sealed record AttachmentFile(
    Guid AttachmentId,
    string FileName,
    string ContentType,
    long SizeBytes,
    Stream Content);
