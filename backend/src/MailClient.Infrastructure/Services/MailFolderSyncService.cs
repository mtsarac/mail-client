using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class MailFolderSyncService(
    AppDbContext db,
    ICredentialProtector credentials,
    MailConnectionHelper connections,
    IFileStorage storage,
    MailSyncOptions options,
    ILogger<MailFolderSyncService> logger)
{
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        var accountIds = await db.MailAccounts
            .Where(account => account.IsActive && account.Folders.Any(folder => folder.IsSyncEnabled))
            .Select(account => account.Id)
            .ToListAsync(cancellationToken);

        foreach (var accountId in accountIds)
        {
            try
            {
                await SyncAccountAsync(accountId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mail sync failed for account {AccountId}.", accountId);
            }
        }
    }

    private async Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts
            .Include(item => item.Folders)
            .SingleAsync(item => item.Id == accountId, cancellationToken);
        var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        var password = credentials.Unprotect(account.EncryptedPassword);

        foreach (var folderId in account.Folders.Where(folder => folder.IsSyncEnabled).Select(folder => folder.Id))
        {
            try
            {
                await connections.WithImapAsync(
                    endpoint,
                    account.Username,
                    password,
                    "SyncFolder",
                    async (client, ct) =>
                    {
                        await SyncFolderAsync(client, account.Id, folderId, ct);
                        return true;
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Mail sync failed for account {AccountId}, folder {FolderId}, host {Host}.",
                    account.Id,
                    folderId,
                    account.ImapHost);
            }
        }
    }

    private async Task SyncFolderAsync(
        ImapClient client,
        Guid accountId,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        await using var folderLock = await FolderAdvisoryLock.AcquireAsync(db, folderId, cancellationToken);
        var localFolder = await db.MailFolders
            .Include(folder => folder.SyncState)
            .SingleAsync(folder => folder.Id == folderId, cancellationToken);
        var remoteFolder = await client.GetFolderAsync(localFolder.FullName, cancellationToken);
        await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var state = localFolder.SyncState;
        if (state is null)
        {
            state = new SyncState
            {
                Id = Guid.NewGuid(),
                MailFolderId = folderId,
                UidValidity = remoteFolder.UidValidity
            };
            db.SyncStates.Add(state);
        }
        else if (SyncStateDecision.RequiresReset(state.UidValidity, remoteFolder.UidValidity))
        {
            var obsoletePaths = await db.Attachments
                .Where(attachment => db.Mails.Any(mail => mail.Id == attachment.MailId && mail.MailFolderId == folderId))
                .Select(attachment => attachment.StoragePath)
                .ToListAsync(cancellationToken);
            db.Mails.RemoveRange(db.Mails.Where(mail => mail.MailFolderId == folderId));
            state.UidValidity = remoteFolder.UidValidity;
            state.LastUid = 0;
            await db.SaveChangesAsync(cancellationToken);
            foreach (var path in obsoletePaths)
            {
                try
                {
                    await storage.DeleteAsync(path, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to remove obsolete attachment for folder {FolderId}.", folderId);
                }
            }
        }

        var uids = await remoteFolder.SearchAsync(
            SearchQuery.Uids(new UniqueIdRange(new UniqueId(state.LastUid + 1), new UniqueId(uint.MaxValue))),
            cancellationToken);
        foreach (var uid in uids.OrderBy(item => item.Id).Take(options.MaxMessagesPerRun))
        {
            var message = await remoteFolder.GetMessageAsync(uid, cancellationToken);
            var incoming = IncomingMailMapper.Map(
                message,
                uid.Id,
                remoteFolder.UidValidity,
                accountId,
                folderId,
                false);
            var mail = new Mail
            {
                Id = Guid.NewGuid(),
                MailAccountId = incoming.MailAccountId,
                MailFolderId = incoming.MailFolderId,
                Uid = incoming.Uid,
                UidValidity = incoming.UidValidity,
                MessageId = incoming.MessageId,
                Subject = incoming.Subject,
                FromAddress = incoming.FromAddress,
                FromDisplayName = incoming.FromDisplayName,
                ToAddress = incoming.ToAddress,
                BodyHtml = incoming.BodyHtml,
                BodyText = incoming.BodyText,
                ReceivedAt = incoming.ReceivedAt,
                IsRead = incoming.IsRead,
                HasAttachments = incoming.Attachments.Count > 0
            };
            var messageAttachmentBytes = 0L;
            var createdPaths = new List<string>();

            foreach (var attachment in incoming.Attachments)
            {
                var remaining = options.MaxMessageAttachmentBytes - messageAttachmentBytes;
                if (remaining <= 0)
                {
                    logger.LogWarning("Skipped oversized attachment for account {AccountId}, folder {FolderId}.", accountId, folderId);
                    continue;
                }

                var attachmentId = Guid.NewGuid();
                StoredFile stored;
                try
                {
                    stored = await storage.SaveAsync(
                        accountId,
                        mail.Id,
                        attachmentId,
                        (destination, ct) => attachment.Content.DecodeToAsync(destination, ct),
                        Math.Min(options.MaxAttachmentBytes, remaining),
                        cancellationToken);
                }
                catch (AttachmentLimitExceededException)
                {
                    logger.LogWarning("Skipped oversized attachment for account {AccountId}, folder {FolderId}.", accountId, folderId);
                    continue;
                }
                createdPaths.Add(stored.RelativePath);
                mail.Attachments.Add(new Attachment
                {
                    Id = attachmentId,
                    MailId = mail.Id,
                    FileName = attachment.FileName,
                    ContentType = attachment.ContentType,
                    SizeBytes = stored.SizeBytes,
                    StoragePath = stored.RelativePath,
                    IsInline = attachment.IsInline,
                    ContentId = attachment.ContentId
                });
                messageAttachmentBytes += stored.SizeBytes;
            }

            db.Mails.Add(mail);
            state.LastUid = uid.Id;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                foreach (var path in createdPaths)
                    await storage.DeleteAsync(path, CancellationToken.None);
                throw;
            }
            db.ChangeTracker.Clear();
        }

        state.UidValidity = remoteFolder.UidValidity;
        state.LastNewMailSyncAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
