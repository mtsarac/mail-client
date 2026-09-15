using System.Security.Cryptography;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Infrastructure.Services;

public sealed record MailReadOutcome(bool Found, bool Applied, bool Conflict, bool ProviderError);

public sealed class MailReadService(
    AppDbContext db,
    Mail.IMailFolderClient folders,
    AuditLogger audit,
    ILogger<MailReadService> logger)
{
    public async Task<MailDetailResponse?> GetAsync(Guid accountId, Guid mailId, CancellationToken cancellationToken)
    {
        var mail = await db.Mails.AsNoTracking()
            .Include(item => item.Attachments)
            .Include(item => item.Participants)
            .Include(item => item.Headers)
            .SingleOrDefaultAsync(item => item.Id == mailId && item.MailAccountId == accountId, cancellationToken);
        if (mail is null)
            return null;

        var body = HtmlMailBodyRenderer.Render(mail, mail.BodyHtml);
        var bodyContract = new MailBodyResponse(body.Html, body.HasRemoteContent, body.RemoteContentHosts, body.TrackingPixelHosts);

        return new MailDetailResponse(
            mail.Id,
            mail.MailFolderId,
            mail.MailAccountId,
            mail.Uid,
            mail.MessageId,
            mail.InReplyToMessageId,
            mail.References,
            mail.Subject,
            ToParticipantResponses(mail.Participants, ParticipantType.From),
            ToParticipantResponses(mail.Participants, ParticipantType.To),
            ToParticipantResponses(mail.Participants, ParticipantType.Cc),
            ToParticipantResponses(mail.Participants, ParticipantType.Bcc),
            ToParticipantResponses(mail.Participants, ParticipantType.ReplyTo),
            mail.BodyText,
            bodyContract,
            mail.IsRead,
            mail.Answered,
            mail.Flagged,
            mail.Draft,
            mail.Deleted,
            mail.Recent,
            mail.HasAttachments,
            mail.SentAt,
            mail.ReceivedAt,
            mail.InternalDate,
            mail.Headers.OrderBy(header => header.Name).Select(header => new MailHeaderResponse(header.Name, header.Value)).ToList(),
            mail.Attachments
                .OrderBy(attachment => attachment.FileName)
                .Select(attachment => new AttachmentResponse(
                    attachment.Id,
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.SizeBytes,
                    attachment.IsInline,
                    attachment.ContentId,
                    attachment.ContentDisposition))
                .ToList());
    }

    private static List<MailParticipantResponse> ToParticipantResponses(IEnumerable<MailParticipant> participants, ParticipantType type) =>
        participants
            .Where(participant => participant.Type == type)
            .OrderBy(participant => participant.SortOrder)
            .Select(participant => new MailParticipantResponse(participant.Id, type.ToString(), participant.Address, participant.DisplayName, participant.SortOrder))
            .ToList();

    public async Task<MailReadOutcome> SetReadAsync(
        Guid accountId,
        Guid mailId,
        bool isRead,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var mail = await db.Mails
            .Include(item => item.MailAccount)
            .Include(item => item.MailFolder)
            .SingleOrDefaultAsync(item => item.Id == mailId && item.MailAccountId == accountId, cancellationToken);
        if (mail?.MailAccount is null || mail.MailFolder is null || mail.MailAccount.Status != MailAccountStatus.Active)
            return new MailReadOutcome(false, false, false, false);

        if (mail.IsRead == isRead)
            return new MailReadOutcome(true, false, false, false);

        var account = mail.MailAccount;
        var fullName = mail.MailFolder.FullName;
        try
        {
            await folders.UseFolderAsync(
                account,
                fullName,
                forUpdate: true,
                async (remote, ct) =>
                {
                    if (remote.UidValidity != mail.UidValidity)
                        throw new MailboxStateChangedException();
                    await remote.SetSeenAsync(new UniqueId(mail.Uid), isRead, ct);
                    return true;
                },
                cancellationToken);
        }
        catch (MailboxStateChangedException)
        {
            logger.LogWarning("Read flag not applied for mail {MailId}: UIDVALIDITY changed on folder {FullName}.", mailId, fullName);
            return new MailReadOutcome(true, false, true, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or MailConnectionException)
        {
            logger.LogWarning(ex, "Read flag not applied for mail {MailId}.", mailId);
            return new MailReadOutcome(true, false, false, true);
        }

        mail.IsRead = isRead;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, "mail.read-state-changed", "Mail", mail.Id.ToString(),
            new Dictionary<string, string?> { ["isRead"] = isRead.ToString() }, correlationId, cancellationToken);
        return new MailReadOutcome(true, true, false, false);
    }

    private sealed class MailboxStateChangedException : Exception;
}
