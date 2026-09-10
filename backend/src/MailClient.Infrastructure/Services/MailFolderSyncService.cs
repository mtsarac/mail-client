using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Storage;
using MailKit;
using MailKit.Net.Imap;
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
            // Acquire the folder lock BEFORE opening the IMAP connection so a second
            // instance waits without holding an unnecessary authenticated connection.
            await using var folderLock = await FolderAdvisoryLock.AcquireAsync(db, folderId, cancellationToken);
            try
            {
                var fullName = account.Folders.Single(folder => folder.Id == folderId).FullName;
                await connections.WithImapAsync(
                    endpoint,
                    account.Username,
                    password,
                    "SyncFolder",
                    async (client, ct) =>
                    {
                        var remote = new MailKitRemoteMailFolder(
                            await client.GetFolderAsync(fullName, ct));
                        await remote.OpenAsync(ct);
                        await SyncFolderCoreAsync(account.Id, folderId, remote, ct);
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

    // Testable core: no IMAP connection management, no advisory lock.
    // The caller owns the lock and the open remote folder.
    internal async Task SyncFolderCoreAsync(
        Guid accountId,
        Guid folderId,
        IRemoteMailFolder remote,
        CancellationToken cancellationToken)
    {
        var localFolder = await db.MailFolders
            .Include(folder => folder.SyncState)
            .SingleAsync(folder => folder.Id == folderId, cancellationToken);

        var state = localFolder.SyncState;
        if (state is null)
        {
            state = new SyncState
            {
                Id = Guid.NewGuid(),
                MailFolderId = folderId,
                UidValidity = remote.UidValidity
            };
            db.SyncStates.Add(state);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (SyncStateDecision.RequiresReset(state.UidValidity, remote.UidValidity))
        {
            var obsoletePaths = await db.Attachments
                .Where(attachment => db.Mails.Any(mail => mail.Id == attachment.MailId && mail.MailFolderId == folderId))
                .Select(attachment => attachment.StoragePath)
                .ToListAsync(cancellationToken);
            db.Mails.RemoveRange(db.Mails.Where(mail => mail.MailFolderId == folderId));
            db.SyncSkippedUids.RemoveRange(db.SyncSkippedUids.Where(skip => skip.MailFolderId == folderId));
            state.UidValidity = remote.UidValidity;
            state.LastUid = 0;
            await db.SaveChangesAsync(cancellationToken);
            foreach (var path in obsoletePaths)
            {
                try
                {
                    await storage.DeleteAsync(path, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to remove obsolete attachment for folder {FolderId}.", folderId);
                }
            }
        }

        var uids = await remote.SearchNewAsync(state.LastUid, cancellationToken);
        foreach (var uid in uids.OrderBy(item => item.Id).Take(options.MaxMessagesPerRun))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SyncOneAsync(accountId, folderId, state, remote, uid, cancellationToken);
        }

        state.UidValidity = remote.UidValidity;
        state.LastNewMailSyncAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SyncOneAsync(
        Guid accountId,
        Guid folderId,
        SyncState state,
        IRemoteMailFolder remote,
        UniqueId uid,
        CancellationToken cancellationToken)
    {
        // Idempotent retry: already committed or already skipped -> just ensure checkpoint.
        if (await db.Mails.AnyAsync(
                mail => mail.MailFolderId == folderId && mail.UidValidity == remote.UidValidity && mail.Uid == uid.Id,
                cancellationToken))
        {
            await AdvanceCheckpointAsync(state, uid.Id, cancellationToken);
            return;
        }

        if (await db.SyncSkippedUids.AnyAsync(
                skip => skip.MailFolderId == folderId && skip.Uid == uid.Id,
                cancellationToken))
        {
            await AdvanceCheckpointAsync(state, uid.Id, cancellationToken);
            return;
        }

        var createdPaths = new List<string>();
        try
        {
            var size = await remote.GetSizeAsync(uid, cancellationToken);
            if (size is null)
            {
                await SkipAndAdvanceAsync(state, folderId, uid.Id, "gone", "Message no longer exists on server.",
                    accountId, folderId, cancellationToken);
                return;
            }

            if (size.Value > (ulong)options.MaxMessageBytes)
            {
                logger.LogWarning(
                    "Skipped oversized message {Uid} ({Size} bytes) for account {AccountId}, folder {FolderId}.",
                    uid.Id, size.Value, accountId, folderId);
                await SkipAndAdvanceAsync(state, folderId, uid.Id, "oversized", "Message exceeds MaxMessageBytes.",
                    accountId, folderId, cancellationToken);
                return;
            }

            var message = await remote.GetMessageAsync(uid, cancellationToken);
            var incoming = IncomingMailMapper.Map(
                message,
                uid.Id,
                remote.UidValidity,
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

            foreach (var attachment in incoming.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                catch (OperationCanceledException)
                {
                    throw;
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
                Detach(mail);
                await CleanupCreatedFilesAsync(createdPaths);
                throw;
            }

            Detach(mail);
        }
        catch (OperationCanceledException)
        {
            await CleanupCreatedFilesAsync(createdPaths);
            throw;
        }
        catch (Exception ex)
        {
            await CleanupCreatedFilesAsync(createdPaths);
            // Permanent-looking failure for this UID: record a durable skip and move
            // the checkpoint past it so later UIDs are not blocked. If the skip
            // itself cannot be persisted (transient DB failure), rethrow and keep
            // the checkpoint unmoved for a retry next poll.
            logger.LogWarning(ex,
                "Skipping message {Uid} for account {AccountId}, folder {FolderId}: {Error}.",
                uid.Id, accountId, folderId, ex.GetType().Name);
            await SkipAndAdvanceAsync(state, folderId, uid.Id, "failed", ex.GetType().Name,
                accountId, folderId, cancellationToken);
        }
    }

    private async Task AdvanceCheckpointAsync(SyncState state, uint uid, CancellationToken cancellationToken)
    {
        if (uid > state.LastUid)
        {
            state.LastUid = uid;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SkipAndAdvanceAsync(
        SyncState state,
        Guid folderId,
        uint uid,
        string kind,
        string detail,
        Guid accountId,
        Guid logFolderId,
        CancellationToken cancellationToken)
    {
        var exists = await db.SyncSkippedUids.AnyAsync(
            skip => skip.MailFolderId == folderId && skip.Uid == uid,
            cancellationToken);
        if (!exists)
        {
            db.SyncSkippedUids.Add(new SyncSkippedUid
            {
                Id = Guid.NewGuid(),
                MailFolderId = folderId,
                Uid = uid,
                Reason = MailFieldNormalizer.Truncate($"{kind}: {detail}", 500),
                SkippedAt = DateTime.UtcNow
            });
        }

        if (uid > state.LastUid)
            state.LastUid = uid;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Marked UID {Uid} as skipped ({Kind}) for account {AccountId}, folder {FolderId}.",
            uid, kind, accountId, logFolderId);
    }

    private async Task CleanupCreatedFilesAsync(List<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                await storage.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up attachment file after sync failure.");
            }
        }
    }

    private void Detach(Mail mail)
    {
        foreach (var attachment in mail.Attachments)
            db.Entry(attachment).State = EntityState.Detached;
        db.Entry(mail).State = EntityState.Detached;
    }
}
