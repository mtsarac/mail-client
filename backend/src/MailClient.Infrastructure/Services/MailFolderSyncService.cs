using System.Security.Cryptography;
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
            .Where(account => account.IsActive && account.Folders.Any(folder => folder.IsSyncEnabled && folder.IsAvailable))
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
            catch (CryptographicException ex)
            {
                logger.LogError(ex,
                    "Mail sync failed for account {AccountId}: stored credential cannot be decrypted. Re-enter the mailbox password.",
                    accountId);
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
            .SingleOrDefaultAsync(item => item.Id == accountId, cancellationToken);
        // Account may have been deactivated or deleted after the poll listed it.
        if (account is null || !account.IsActive)
            return;
        var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        var password = credentials.Unprotect(account.EncryptedPassword);

        foreach (var (folderId, fullName) in await GetSyncableFoldersAsync(accountId, cancellationToken))
        {
            // Acquire the folder lock BEFORE opening the IMAP connection so a second
            // instance waits without holding an unnecessary authenticated connection.
            await using var folderLock = await FolderAdvisoryLock.AcquireAsync(db, folderId, cancellationToken);
            try
            {
                // Re-check under the lock: deletion holds this lock while removing
                // the account, so a missing account here means deletion won.
                if (!await db.MailAccounts.AnyAsync(item => item.Id == accountId, cancellationToken))
                    return;
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

    // Folders eligible for sync: user-enabled and last seen by discovery.
    // Unavailable (stale) folders are never synchronized; their mail is kept.
    internal async Task<IReadOnlyList<(Guid FolderId, string FullName)>> GetSyncableFoldersAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        return (await db.MailFolders
            .AsNoTracking()
            .Where(folder => folder.MailAccountId == accountId && folder.IsSyncEnabled && folder.IsAvailable)
            .Select(folder => new { folder.Id, folder.FullName })
            .ToListAsync(cancellationToken))
            .Select(folder => (folder.Id, folder.FullName))
            .ToList();
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
            state.NextUidScanStart = 1;
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

        // Scan cursor invariants (persisted in NextUidScanStart, a long):
        // - LastUid = highest UID safely processed; never advanced over unsearched ranges.
        // - The cursor only moves forward over UID ranges SEARCH actually covered,
        //   or past fully processed UIDs, so every UID above LastUid stays reachable.
        // - A fully scanned 32-bit UID space is the one-past-end sentinel
        //   (uint.MaxValue + 1); the derived IMAP search point (cursor - 1)
        //   always stays inside uint range, so there is no wraparound.
        // - New arrivals always land at or beyond UidNext, hence ahead of the cursor.
        var afterUid = state.NextUidScanStart <= 1
            ? 0u
            : (uint)Math.Min(state.NextUidScanStart - 1, (long)uint.MaxValue);
        var result = await remote.SearchNewAsync(
            afterUid, options.MaxMessagesPerRun, cancellationToken);
        var batch = result.Uids.OrderBy(item => item.Id).Take(options.MaxMessagesPerRun).ToList();
        if (batch.Count == 0)
        {
            state.UidValidity = remote.UidValidity;
            state.LastNewMailSyncAt = DateTime.UtcNow;
            state.NextUidScanStart = CursorAfter(result.ScannedUpTo);
            await ReconcileFlagsIfDueAsync(folderId, state, remote, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var batchIds = batch.Select(item => item.Id).ToList();
        var committedUids = (await db.Mails
            .Where(mail => mail.MailFolderId == folderId
                && mail.UidValidity == remote.UidValidity
                && batchIds.Contains(mail.Uid))
            .Select(mail => mail.Uid)
            .ToListAsync(cancellationToken)).ToHashSet();
        var skippedUids = (await db.SyncSkippedUids
            .Where(skip => skip.MailFolderId == folderId && batchIds.Contains(skip.Uid))
            .Select(skip => skip.Uid)
            .ToListAsync(cancellationToken)).ToHashSet();
        var summaries = await remote.GetSummariesAsync(batch, cancellationToken);

        foreach (var uid in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SyncOneAsync(accountId, folderId, state, remote, uid,
                summaries.GetValueOrDefault(uid.Id), committedUids, skippedUids, cancellationToken);
        }

        state.UidValidity = remote.UidValidity;
        state.LastNewMailSyncAt = DateTime.UtcNow;
        state.NextUidScanStart = CursorAfter(batch.Max(item => item.Id));
        await ReconcileFlagsIfDueAsync(folderId, state, remote, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Cursor advance without wraparound. The one-past-end sentinel
    // (uint.MaxValue + 1) is representable because the cursor is a long;
    // it must not be clamped back to uint.MaxValue, or a fully synced
    // mailbox would rescan its last UID forever.
    private static long CursorAfter(uint scannedMaxUid) =>
        scannedMaxUid == uint.MaxValue ? (long)uint.MaxValue + 1 : (long)scannedMaxUid + 1;

    // IMAP is the source of truth for \Seen. Reconciliation runs at most every
    // FlagSyncIntervalSeconds per folder and only stamps LastFlagSyncAt after a
    // fully successful pass. Failures never touch LastUid or checkpoints.
    private async Task ReconcileFlagsIfDueAsync(
        Guid folderId,
        SyncState state,
        IRemoteMailFolder remote,
        CancellationToken cancellationToken)
    {
        var due = state.LastFlagSyncAt is null
            || DateTime.UtcNow - state.LastFlagSyncAt.Value >= TimeSpan.FromSeconds(options.FlagSyncIntervalSeconds);
        if (!due)
            return;

        try
        {
            await ReconcileFlagsAsync(folderId, remote.UidValidity, remote, cancellationToken);
            state.LastFlagSyncAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Flag reconciliation failed for folder {FolderId}.", folderId);
        }
    }

    private async Task ReconcileFlagsAsync(
        Guid folderId,
        uint uidValidity,
        IRemoteMailFolder remote,
        CancellationToken cancellationToken)
    {
        const int chunkSize = 500;
        var lastUid = 0u;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = await db.Mails
                .AsNoTracking()
                .Where(mail => mail.MailFolderId == folderId && mail.UidValidity == uidValidity && mail.Uid > lastUid)
                .OrderBy(mail => mail.Uid)
                .Take(chunkSize)
                .Select(mail => new { mail.Id, mail.Uid, mail.IsRead })
                .ToListAsync(cancellationToken);
            if (chunk.Count == 0)
                return;

            var flags = await remote.GetFlagsAsync(
                chunk.Select(item => new UniqueId(item.Uid)).ToList(), cancellationToken);
            var toRead = new List<Guid>();
            var toUnread = new List<Guid>();
            foreach (var item in chunk)
            {
                if (!flags.TryGetValue(item.Uid, out var seen))
                    continue;
                if (seen && !item.IsRead)
                    toRead.Add(item.Id);
                else if (!seen && item.IsRead)
                    toUnread.Add(item.Id);
            }

            if (toRead.Count > 0)
            {
                var mails = await db.Mails
                    .Where(mail => mail.MailFolderId == folderId
                        && mail.UidValidity == uidValidity
                        && toRead.Contains(mail.Id))
                    .ToListAsync(cancellationToken);
                foreach (var mail in mails)
                    mail.IsRead = true;
                await db.SaveChangesAsync(cancellationToken);
                foreach (var mail in mails)
                    db.Entry(mail).State = EntityState.Detached;
            }

            if (toUnread.Count > 0)
            {
                var mails = await db.Mails
                    .Where(mail => mail.MailFolderId == folderId
                        && mail.UidValidity == uidValidity
                        && toUnread.Contains(mail.Id))
                    .ToListAsync(cancellationToken);
                foreach (var mail in mails)
                    mail.IsRead = false;
                await db.SaveChangesAsync(cancellationToken);
                foreach (var mail in mails)
                    db.Entry(mail).State = EntityState.Detached;
            }

            lastUid = chunk[^1].Uid;
        }
    }

    private async Task SyncOneAsync(
        Guid accountId,
        Guid folderId,
        SyncState state,
        IRemoteMailFolder remote,
        UniqueId uid,
        RemoteSummary? summary,
        HashSet<uint> committedUids,
        HashSet<uint> skippedUids,
        CancellationToken cancellationToken)
    {
        if (committedUids.Contains(uid.Id) || skippedUids.Contains(uid.Id))
        {
            await AdvanceCheckpointAsync(state, uid.Id, cancellationToken);
            return;
        }

        var checkpointBefore = state.LastUid;
        var createdPaths = new List<string>();
        try
        {
            if (summary is null)
            {
                await SkipAndAdvanceAsync(state, folderId, uid.Id, "gone", "Message no longer exists on server.",
                    accountId, folderId, cancellationToken);
                return;
            }

            if (summary.Size > (ulong)options.MaxMessageBytes)
            {
                logger.LogWarning(
                    "Skipped oversized message {Uid} ({Size} bytes) for account {AccountId}, folder {FolderId}.",
                    uid.Id, summary.Size, accountId, folderId);
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
                summary.IsSeen);
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
                IsRead = incoming.IsRead
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

            mail.HasAttachments = mail.Attachments.Count > 0;

            db.Mails.Add(mail);
            state.LastUid = uid.Id;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                Detach(mail);
                throw;
            }

            Detach(mail);
        }
        catch (OperationCanceledException)
        {
            state.LastUid = checkpointBefore;
            await CleanupCreatedFilesAsync(createdPaths);
            throw;
        }
        catch (Exception ex) when (SyncFailurePolicy.Classify(ex) == SyncFailureDisposition.Skip)
        {
            await CleanupCreatedFilesAsync(createdPaths);
            logger.LogWarning(ex,
                "Skipping message {Uid} for account {AccountId}, folder {FolderId}: {Error}.",
                uid.Id, accountId, folderId, ex.GetType().Name);
            try
            {
                await SkipAndAdvanceAsync(state, folderId, uid.Id, "failed", ex.GetType().Name,
                    accountId, folderId, cancellationToken);
            }
            catch
            {
                state.LastUid = checkpointBefore;
                throw;
            }
        }
        catch (Exception ex)
        {
            state.LastUid = checkpointBefore;
            await CleanupCreatedFilesAsync(createdPaths);
            logger.LogWarning(ex,
                "Transient sync failure for message {Uid} for account {AccountId}, folder {FolderId}: {Error}. Retrying next poll.",
                uid.Id, accountId, folderId, ex.GetType().Name);
            throw;
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
