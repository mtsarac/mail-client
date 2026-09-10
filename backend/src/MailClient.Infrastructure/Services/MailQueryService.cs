using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class MailQueryService(
    AppDbContext db,
    IFileStorage storage,
    ILogger<MailQueryService> logger) : IMailQueryService
{
    private const int MaxPageSize = 100;

    public async Task<ServiceResult<MailPageDto>> ListAsync(
        Guid userId,
        MailListQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Page < 1 || query.PageSize < 1 || query.PageSize > MaxPageSize)
            return ServiceResult<MailPageDto>.Failure(
                ServiceOutcome.Invalid, "page", "Page must be >= 1 and pageSize between 1 and 100.");

        if (query.AccountId is { } accountId
            && !await db.MailAccounts.AnyAsync(
                account => account.Id == accountId && account.UserId == userId, cancellationToken))
            return ServiceResult<MailPageDto>.Failure(ServiceOutcome.NotFound, "account", "Mail account not found.");

        if (query.FolderId is { } folderId
            && !await db.MailFolders.AnyAsync(
                folder => folder.Id == folderId && folder.MailAccount!.UserId == userId, cancellationToken))
            return ServiceResult<MailPageDto>.Failure(ServiceOutcome.NotFound, "folder", "Mail folder not found.");

        var mails = db.Mails
            .AsNoTracking()
            .Where(mail => mail.MailAccount!.UserId == userId);
        if (query.AccountId is { } accountFilter)
            mails = mails.Where(mail => mail.MailAccountId == accountFilter);
        if (query.FolderId is { } folderFilter)
            mails = mails.Where(mail => mail.MailFolderId == folderFilter);
        if (query.FolderType is { } typeFilter)
            mails = mails.Where(mail => mail.MailFolder!.FolderType == typeFilter);

        var ordered = mails
            .OrderByDescending(mail => mail.ReceivedAt)
            .ThenByDescending(mail => mail.Id);
        var totalCount = await ordered.CountAsync(cancellationToken);
        var items = await ordered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(mail => new MailSummaryDto(
                mail.Id,
                mail.MailAccountId,
                mail.MailAccount!.EmailAddress,
                mail.MailFolderId,
                mail.MailFolder!.FolderType,
                mail.FromDisplayName,
                mail.FromAddress,
                mail.Subject,
                mail.ReceivedAt,
                mail.IsRead,
                mail.HasAttachments))
            .ToListAsync(cancellationToken);

        return ServiceResult<MailPageDto>.Success(new MailPageDto(items, totalCount, query.Page, query.PageSize));
    }

    public async Task<ServiceResult<MailDetailDto>> GetDetailAsync(
        Guid userId,
        Guid mailId,
        CancellationToken cancellationToken)
    {
        var detail = await db.Mails
            .AsNoTracking()
            .Where(mail => mail.Id == mailId && mail.MailAccount!.UserId == userId)
            .Select(mail => new MailDetailDto(
                mail.Id,
                mail.MailAccountId,
                mail.MailAccount!.EmailAddress,
                mail.MailFolderId,
                mail.MailFolder!.FolderType,
                mail.MessageId,
                mail.Subject,
                mail.FromAddress,
                mail.FromDisplayName,
                mail.ToAddress,
                mail.BodyHtml,
                mail.BodyText,
                mail.ReceivedAt,
                mail.IsRead,
                mail.HasAttachments,
                mail.Attachments
                    .OrderBy(attachment => attachment.Id)
                    .Select(attachment => new AttachmentDto(
                        attachment.Id,
                        attachment.FileName,
                        attachment.ContentType,
                        attachment.SizeBytes,
                        attachment.IsInline,
                        attachment.ContentId))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);

        return detail is null
            ? ServiceResult<MailDetailDto>.Failure(ServiceOutcome.NotFound, "mail", "Mail not found.")
            : ServiceResult<MailDetailDto>.Success(detail);
    }

    public async Task<ServiceResult<AttachmentFile>> GetAttachmentAsync(
        Guid userId,
        Guid mailId,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        var meta = await db.Attachments
            .AsNoTracking()
            .Where(attachment => attachment.Id == attachmentId
                && attachment.MailId == mailId
                && attachment.Mail!.MailAccount!.UserId == userId)
            .Select(attachment => new
            {
                attachment.FileName,
                attachment.ContentType,
                attachment.SizeBytes,
                attachment.StoragePath
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (meta is null)
            return ServiceResult<AttachmentFile>.Failure(
                ServiceOutcome.NotFound, "attachment", "Attachment not found.");

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(meta.StoragePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogError(ex, "Attachment file missing for attachment {AttachmentId}.", attachmentId);
            return ServiceResult<AttachmentFile>.Failure(
                ServiceOutcome.NotFound, "attachment", "Attachment not found.");
        }

        return ServiceResult<AttachmentFile>.Success(new AttachmentFile(
            attachmentId, meta.FileName, meta.ContentType, meta.SizeBytes, content));
    }
}
