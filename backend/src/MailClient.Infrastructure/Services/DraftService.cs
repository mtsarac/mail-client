using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Storage;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using MailFolderEntity = MailClient.Domain.Entities.MailFolder;

namespace MailClient.Infrastructure.Services;

public sealed class DraftService(
    AppDbContext db,
    IMailFolderClient folders,
    ISyncExecutor sync,
    MailReadService reader,
    IMailOperationService operations,
    AuditLogger audit,
    ILogger<DraftService> logger)
{
    public async Task<DraftWriteResult> CreateAsync(Guid accountId, DraftCommand command, string? correlationId, CancellationToken cancellationToken)
    {
        var (account, folder) = await GetAccountAndDraftsAsync(accountId, cancellationToken);
        var message = await BuildAsync(account, command, null, cancellationToken);
        var append = await AppendAsync(account, folder, message, cancellationToken);
        var messageId = message.MessageId ?? throw new InvalidOperationException("message_not_constructible");
        var mailId = await ReconcileAndFindAsync(accountId, folder, messageId, append, null, cancellationToken);
        await audit.WriteAsync(accountId, "draft.created", "Mail", mailId?.ToString(), null, correlationId, cancellationToken);
        return new(true, mailId, mailId is null, null);
    }

    public async Task<DraftWriteResult> UpdateAsync(Guid accountId, Guid draftId, DraftCommand command, string? correlationId, CancellationToken cancellationToken)
    {
        var source = await GetDraftEntityAsync(accountId, draftId, cancellationToken);
        var (account, folder) = await GetAccountAndDraftsAsync(accountId, cancellationToken);
        var message = await BuildAsync(account, command, source, cancellationToken);
        var append = await AppendAsync(account, folder, message, cancellationToken);
        var messageId = message.MessageId ?? throw new InvalidOperationException("message_not_constructible");
        var replacementId = await ReconcileAndFindAsync(accountId, folder, messageId, append, draftId, cancellationToken);
        if (replacementId is null)
            return new(false, null, true, "Draft replacement stored; refresh Drafts before retrying cleanup.");
        await MoveToTrashAsync(accountId, draftId, correlationId, cancellationToken);
        await audit.WriteAsync(accountId, "draft.updated", "Mail", replacementId.Value.ToString(), null, correlationId, cancellationToken);
        return new(false, replacementId, false, null);
    }

    public async Task<DraftLookupResult> GetAsync(Guid accountId, Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await db.Mails.AsNoTracking()
            .Include(mail => mail.MailFolder)
            .SingleOrDefaultAsync(mail => mail.Id == draftId && mail.MailAccountId == accountId, cancellationToken);
        if (draft is null)
            return new(null, DraftLookupError.NotFound);
        if (draft.MailFolder?.FolderType != MailFolderType.Drafts || !draft.Draft)
            return new(null, DraftLookupError.NotDraft);
        return new(await reader.GetAsync(accountId, draftId, cancellationToken), DraftLookupError.None);
    }

    public async Task<DraftSendResult> SendAsync(Guid accountId, Guid draftId, string idempotencyKey, MailSendService sender, IFileStorage storage, string? correlationId, CancellationToken cancellationToken)
    {
        var draft = await GetDraftEntityAsync(accountId, draftId, cancellationToken);
        await db.Entry(draft).Collection(mail => mail.Participants).LoadAsync(cancellationToken);
        await db.Entry(draft).Collection(mail => mail.Attachments).LoadAsync(cancellationToken);
        var attachments = new List<SendMailAttachment>();
        try
        {
            foreach (var attachment in draft.Attachments)
                attachments.Add(new(attachment.FileName, attachment.ContentType, await storage.OpenReadAsync(attachment.StoragePath, cancellationToken)));
            var command = new SendMailCommand(
                accountId,
                Participants(draft, ParticipantType.To),
                Participants(draft, ParticipantType.Cc),
                Participants(draft, ParticipantType.Bcc),
                draft.Subject,
                draft.BodyHtml,
                draft.BodyText,
                attachments)
            {
                IdempotencyKey = idempotencyKey,
                TrustedMessageId = draft.MessageId,
                TrustedInReplyToMessageId = draft.InReplyToMessageId,
                TrustedReferences = draft.References
            };
            var result = await sender.SendAsync(accountId, command, correlationId, cancellationToken);
            if (!result.Sent)
                return new(false, result.SentCopySaved, false, result.Warning);
            try
            {
                await MoveToTrashAsync(accountId, draftId, correlationId, CancellationToken.None);
                return new(true, result.SentCopySaved, true, result.Warning, result.MailId, result.ConversationId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Draft cleanup failed after successful delivery for draft {DraftId}.", draftId);
                var warning = string.IsNullOrWhiteSpace(result.Warning)
                    ? "Message was sent, but draft cleanup failed."
                    : $"{result.Warning} Draft cleanup failed.";
                return new(true, result.SentCopySaved, false, warning, result.MailId, result.ConversationId);
            }
        }
        catch
        {
            foreach (var attachment in attachments)
                await attachment.Content.DisposeAsync();
            throw;
        }
    }

    public async Task<DraftDeleteResult> DeleteAsync(Guid accountId, Guid draftId, string? correlationId, CancellationToken cancellationToken)
    {
        await GetDraftEntityAsync(accountId, draftId, cancellationToken);
        await MoveToTrashAsync(accountId, draftId, correlationId, cancellationToken);
        await audit.WriteAsync(accountId, "draft.deleted", "Mail", draftId.ToString(), null, correlationId, cancellationToken);
        return new(true, null);
    }

    private async Task<(MailAccount Account, MailFolderEntity Folder)> GetAccountAndDraftsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts.SingleOrDefaultAsync(account => account.Id == accountId && account.Status == MailAccountStatus.Active, cancellationToken)
            ?? throw new InvalidOperationException("mail_account_not_found");
        var folder = await db.MailFolders
            .Where(folder => folder.MailAccountId == accountId && folder.FolderType == MailFolderType.Drafts && folder.IsAvailable)
            .OrderBy(folder => folder.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("drafts_folder_unavailable");
        return (account, folder);
    }

    private async Task<MailClient.Domain.Entities.Mail> GetDraftEntityAsync(Guid accountId, Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await db.Mails.Include(mail => mail.MailFolder).Include(mail => mail.MailAccount)
            .SingleOrDefaultAsync(mail => mail.Id == draftId && mail.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("draft_not_found");
        if (draft.MailFolder?.FolderType != MailFolderType.Drafts || !draft.Draft)
            throw new InvalidOperationException("mail_not_draft");
        return draft;
    }

    private async Task<MimeMessage> BuildAsync(MailAccount account, DraftCommand command, MailClient.Domain.Entities.Mail? existing, CancellationToken cancellationToken)
    {
        var recipients = ParseDraftRecipients(command);
        var threading = existing is null
            ? await ResolveThreadingAsync(account.Id, command.ReplySourceMailId, cancellationToken)
            : new(existing.InReplyToMessageId, existing.References);
        return MimeMessageBuilder.Build(
            account.EmailAddress,
            account.DisplayName,
            recipients.To.Select(ToMailbox).ToList(),
            recipients.Cc.Select(ToMailbox).ToList(),
            recipients.Bcc.Select(ToMailbox).ToList(),
            MailFieldNormalizer.Subject(command.Subject),
            command.BodyHtml,
            command.BodyText,
            command.Attachments,
            threading.InReplyTo,
            threading.References,
            existing?.MessageId);
    }

    private async Task<RemoteAppendResult> AppendAsync(MailAccount account, MailFolderEntity folder, MimeMessage message, CancellationToken cancellationToken) =>
        await folders.UseFolderAsync(account, folder.FullName, true, (remote, ct) => remote.AppendAsync(message, MessageFlags.Draft, ct), cancellationToken);

    private async Task<Guid?> ReconcileAndFindAsync(Guid accountId, MailFolderEntity folder, string messageId, RemoteAppendResult append, Guid? sourceDraftId, CancellationToken cancellationToken)
    {
        await sync.SyncFolderAsync(accountId, folder.Id, cancellationToken);
        var query = db.Mails.AsNoTracking()
            .Where(mail => mail.MailAccountId == accountId && mail.MailFolderId == folder.Id && mail.MessageId == messageId);
        if (sourceDraftId is not null)
            query = query.Where(mail => mail.Id != sourceDraftId.Value);
        if (append.DestinationUid.HasValue)
        {
            var uid = append.DestinationUid.Value.Id;
            return await query
                .Where(mail => mail.Uid == uid && mail.UidValidity == append.DestinationUidValidity)
                .Select(mail => (Guid?)mail.Id)
                .SingleOrDefaultAsync(cancellationToken);
        }

        var candidates = await query
            .OrderByDescending(mail => mail.InternalDate)
            .ThenByDescending(mail => mail.ReceivedAt)
            .ThenByDescending(mail => mail.Uid)
            .ThenByDescending(mail => mail.Id)
            .Select(mail => mail.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static ResolvedRecipients ParseDraftRecipients(DraftCommand command)
    {
        var hasRecipients = command.To.Concat(command.Cc).Concat(command.Bcc).Any(value => !string.IsNullOrWhiteSpace(value));
        return hasRecipients ? MailRecipientResolver.Parse(command.To, command.Cc, command.Bcc) : ResolvedRecipients.Empty;
    }

    private async Task MoveToTrashAsync(Guid accountId, Guid draftId, string? correlationId, CancellationToken cancellationToken)
    {
        var result = await operations.ExecuteAsync(accountId, new MailOperationRequest(draftId, MailOperationKind.Trash), correlationId, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException(result.Error == MailOperationError.FolderNotFound ? "trash_folder_unavailable" : "draft_delete_failed");
    }

    private async Task<(string? InReplyTo, string? References)> ResolveThreadingAsync(Guid accountId, Guid? sourceMailId, CancellationToken cancellationToken)
    {
        if (sourceMailId is null)
            return (null, null);
        var source = await db.Mails.AsNoTracking().SingleOrDefaultAsync(mail => mail.Id == sourceMailId && mail.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("mail_not_found");
        var references = MailClient.Application.Conversations.ConversationEngine.ParseReferences(source.References)
            .Append(MailClient.Application.Conversations.ConversationEngine.NormalizeMessageId(source.MessageId))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return (MailClient.Application.Conversations.ConversationEngine.NormalizeMessageId(source.MessageId), string.Join(' ', references));
    }

    private static IReadOnlyList<string> Participants(MailClient.Domain.Entities.Mail draft, ParticipantType type) =>
        draft.Participants
            .Where(participant => participant.Type == type)
            .OrderBy(participant => participant.SortOrder)
            .Select(participant => participant.Address)
            .ToList();

    private static MailboxAddress ToMailbox(MailRecipient recipient) => new(recipient.DisplayName, recipient.Address);
}
