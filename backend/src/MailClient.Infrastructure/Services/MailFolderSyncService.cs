using System.Security.Cryptography;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Application.Sync;
using MailClient.Infrastructure.Runtime;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Infrastructure.Services;

public sealed class MailFolderSyncService(
    AppDbContext db,
    MailCredentialResolver credentials,
    Mail.MailConnectionHelper connections,
    IFileStorage storage,
    RuntimeOperationSettings operationSettings,
    IPushNotificationService push,
    ConversationService conversations,
    MailReconciliationService reconciliations,
    ILogger<MailFolderSyncService> logger) : ISyncExecutor
{
    private async Task MarkReauthenticationAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts.SingleOrDefaultAsync(item => item.Id == accountId, cancellationToken);
        if (account is null)
            return;
        var alreadyNotified = account.Status == MailAccountStatus.NeedsReauthentication;
        account.Status = MailAccountStatus.NeedsReauthentication;
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (alreadyNotified)
            return;
        try
        {
            await push.NotifyAsync(new PushEvent(PushEventType.AccountReauthenticationRequired, accountId), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reauthentication push failed. Account state is unaffected.");
        }
    }

    /// <summary>
    /// Syncs one available folder of an active account. Credential problems move the account to
    /// NeedsReauthentication instead of failing; any other failure propagates to the caller (coordinator retry policy).
    /// </summary>
    public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == accountId, cancellationToken);
        if (account is null || account.Status != MailAccountStatus.Active)
            return;
        var fullName = await db.MailFolders.AsNoTracking()
            .Where(item => item.Id == folderId && item.MailAccountId == accountId && item.IsAvailable)
            .Select(item => item.FullName)
            .SingleOrDefaultAsync(cancellationToken);
        if (fullName is null)
            return;

        ResolvedCredential resolved;
        try
        {
            resolved = await credentials.ResolveAsync(accountId, cancellationToken);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Mail sync failed because the stored credential cannot be decrypted.");
            await MarkReauthenticationAsync(accountId, cancellationToken);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message is "credential_missing" or "mail_account_not_found" or "mail_account_disabled")
        {
            logger.LogWarning(ex, "Mail sync skipped because the credential is unavailable.");
            await MarkReauthenticationAsync(accountId, cancellationToken);
            return;
        }

        var endpoint = new MailServerEndpoint(resolved.Account.ImapHost, resolved.Account.ImapPort, resolved.Account.ImapSecurity);
        try
        {
            await connections.WithImapAsync(
                endpoint,
                resolved.Username,
                resolved.Secret,
                "SyncFolder",
                async (client, ct) =>
                {
                    var remote = new MailKitRemoteMailFolder(await client.GetFolderAsync(fullName, ct));
                    await remote.OpenAsync(ct);
                    await SyncFolderCoreAsync(accountId, folderId, remote, ct);
                    return true;
                },
                cancellationToken,
                resolved.AuthenticationMethod);
        }
        catch (MailConnectionException ex) when (ex.Failure == MailConnectionFailure.Authentication)
        {
            logger.LogWarning(ex, "Mail sync failed because the provider rejected the credential.");
            await MarkReauthenticationAsync(accountId, cancellationToken);
        }
    }

    internal async Task SyncFolderCoreAsync(
        Guid accountId,
        Guid folderId,
        IRemoteMailFolder remote,
        CancellationToken cancellationToken)
    {
        // Loads the runtime limits (operationSettings.Current) used below for this scope.
        await operationSettings.GetAsync(cancellationToken);
        var localFolder = await db.MailFolders
            .Include(folder => folder.SyncState)
            .SingleAsync(folder => folder.Id == folderId, cancellationToken);

        var state = localFolder.SyncState;
        if (state is null)
        {
            state = await CreateSyncStateAsync(accountId, folderId, remote, cancellationToken);
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
            state.FlagScanCursorUid = 0;
            state.BackfillNextUid = 0;
            StartNewestFirst(state, remote);
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
                    logger.LogWarning(ex, "Failed to remove an obsolete attachment.");
                }
            }
        }

        var budget = operationSettings.Current.Sync.MaxMessagesPerRun;
        var afterUid = state.NextUidScanStart <= 1
            ? 0u
            : (uint)Math.Min(state.NextUidScanStart - 1, (long)uint.MaxValue);
        var result = await remote.SearchNewAsync(afterUid, budget, cancellationToken);
        var batch = result.Uids.OrderBy(item => item.Id).Take(budget).ToList();
        await ImportAsync(accountId, folderId, state, remote, batch, true, cancellationToken);

        state.UidValidity = remote.UidValidity;
        state.LastNewMailSyncAt = DateTime.UtcNow;
        state.NextUidScanStart = batch.Count == 0
            ? CursorAfter(result.ScannedUpTo)
            : CursorAfter(batch.Max(item => item.Id));
        await BackfillOlderAsync(accountId, folderId, state, remote, budget - batch.Count, cancellationToken);
        await ReconcileFlagsIfDueAsync(folderId, state, remote, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal async Task<int> ImportRemoteMatchesAsync(
        Guid accountId,
        Guid folderId,
        IRemoteMailFolder remote,
        IReadOnlyList<UniqueId> uids,
        CancellationToken cancellationToken)
    {
        await operationSettings.GetAsync(cancellationToken);
        var localFolder = await db.MailFolders
            .Include(folder => folder.SyncState)
            .SingleAsync(folder => folder.Id == folderId && folder.MailAccountId == accountId, cancellationToken);
        var state = localFolder.SyncState ?? await CreateSyncStateAsync(accountId, folderId, remote, cancellationToken);
        if (state.UidValidity != remote.UidValidity)
            return 0;

        var imported = await ImportAsync(accountId, folderId, state, remote, uids, false, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return imported.Count;
    }

    private async Task<SyncState> CreateSyncStateAsync(
        Guid accountId,
        Guid folderId,
        IRemoteMailFolder remote,
        CancellationToken cancellationToken)
    {
        var state = new SyncState
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            MailFolderId = folderId,
            UidValidity = remote.UidValidity
        };
        StartNewestFirst(state, remote);
        db.SyncStates.Add(state);
        await db.SaveChangesAsync(cancellationToken);
        return state;
    }

    /// <summary>
    /// A folder is synced newest-first: forward scanning starts at the current UIDNEXT and the existing history
    /// is walked backwards separately, so a large mailbox surfaces recent mail on the first poll. Servers that do
    /// not report UIDNEXT keep the original oldest-first behaviour.
    /// </summary>
    private static void StartNewestFirst(SyncState state, IRemoteMailFolder remote)
    {
        var uidNext = remote.UidNext;
        if (uidNext <= 1)
            return;
        state.NextUidScanStart = uidNext;
        state.BackfillNextUid = uidNext;
    }

    private async Task BackfillOlderAsync(
        Guid accountId,
        Guid folderId,
        SyncState state,
        IRemoteMailFolder remote,
        int budget,
        CancellationToken cancellationToken)
    {
        if (state.BackfillNextUid <= 1 || budget <= 0)
            return;
        var page = await remote.SearchOlderAsync(state.BackfillNextUid, budget, cancellationToken);
        await ImportAsync(accountId, folderId, state, remote, [.. page.Uids.OrderBy(item => item.Id)], false, cancellationToken);
        state.BackfillNextUid = page.NextHighExclusive;
    }

    private async Task<List<Guid>> ImportAsync(
        Guid accountId,
        Guid folderId,
        SyncState state,
        IRemoteMailFolder remote,
        IReadOnlyList<UniqueId> batch,
        bool isNewMail,
        CancellationToken cancellationToken)
    {
        var imported = new List<Guid>();
        if (batch.Count == 0)
            return imported;

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
            var mailId = await SyncOneAsync(accountId, folderId, state, remote, uid,
                summaries.GetValueOrDefault(uid.Id), committedUids, skippedUids, isNewMail, cancellationToken);
            if (mailId is { } id)
                imported.Add(id);
        }

        return imported;
    }

    private static long CursorAfter(uint scannedMaxUid) =>
        scannedMaxUid == uint.MaxValue ? (long)uint.MaxValue + 1 : (long)scannedMaxUid + 1;

    private async Task ReconcileFlagsIfDueAsync(
        Guid folderId,
        SyncState state,
        IRemoteMailFolder remote,
        CancellationToken cancellationToken)
    {
        // A pass already in progress continues on every poll; a finished pass restarts only when due.
        var due = state.FlagScanCursorUid > 0
            || state.LastFlagSyncAt is null
            || DateTime.UtcNow - state.LastFlagSyncAt.Value >= TimeSpan.FromSeconds(operationSettings.Current.Sync.FlagSyncIntervalSeconds);
        if (!due)
            return;

        try
        {
            await ReconcileFlagsAsync(folderId, state, remote, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Flag reconciliation failed.");
        }
    }

    private const int ReconciliationChunkSize = 500;
    private const int ReconciliationChunksPerRun = 2;

    /// <summary>
    /// Converges local rows with the mailbox in bounded chunks: server flags win, and rows the server no longer
    /// returns for the current UIDVALIDITY were expunged or moved away remotely and are removed locally. Rows in a
    /// pending local move keep their placeholder until their own reconciliation completes.
    /// </summary>
    private async Task ReconcileFlagsAsync(
        Guid folderId,
        SyncState state,
        IRemoteMailFolder remote,
        CancellationToken cancellationToken)
    {
        var uidValidity = remote.UidValidity;
        var cursor = state.FlagScanCursorUid;
        for (var chunkIndex = 0; chunkIndex < ReconciliationChunksPerRun; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = await db.Mails
                .AsNoTracking()
                .Where(mail => mail.MailFolderId == folderId && mail.UidValidity == uidValidity && mail.Uid > cursor)
                .OrderBy(mail => mail.Uid)
                .Take(ReconciliationChunkSize)
                .Select(mail => new ReconciliationRow(
                    mail.Id, mail.Uid, mail.IsRead, mail.Answered, mail.Flagged, mail.Draft, mail.Deleted, mail.ReconciliationState, mail.ExpectedMailFolderId))
                .ToListAsync(cancellationToken);
            if (chunk.Count == 0)
            {
                state.FlagScanCursorUid = 0;
                state.LastFlagSyncAt = DateTime.UtcNow;
                return;
            }

            var flags = await remote.GetFlagsAsync(
                chunk.Select(item => new UniqueId(item.Uid)).ToList(), cancellationToken);
            await ApplyFlagChangesAsync(folderId, uidValidity, chunk, flags, cancellationToken);
            await RemoveVanishedAsync(folderId, uidValidity, chunk, flags, cancellationToken);

            cursor = chunk[^1].Uid;
            state.FlagScanCursorUid = cursor;
            if (chunk.Count < ReconciliationChunkSize)
            {
                state.FlagScanCursorUid = 0;
                state.LastFlagSyncAt = DateTime.UtcNow;
                return;
            }
        }
    }

    private async Task ApplyFlagChangesAsync(
        Guid folderId,
        uint uidValidity,
        IReadOnlyList<ReconciliationRow> chunk,
        IReadOnlyDictionary<uint, RemoteMessageFlags> flags,
        CancellationToken cancellationToken)
    {
        var changed = chunk
            .Where(row => flags.TryGetValue(row.Uid, out var remote) && row.Differs(remote))
            .Select(row => row.Id)
            .ToList();
        if (changed.Count == 0)
            return;

        var mails = await db.Mails
            .Where(mail => mail.MailFolderId == folderId && mail.UidValidity == uidValidity && changed.Contains(mail.Id))
            .ToListAsync(cancellationToken);
        foreach (var mail in mails)
        {
            if (!flags.TryGetValue(mail.Uid, out var remoteFlags))
                continue;
            mail.IsRead = remoteFlags.Seen;
            mail.Answered = remoteFlags.Answered;
            mail.Flagged = remoteFlags.Flagged;
            mail.Draft = remoteFlags.Draft;
            mail.Deleted = remoteFlags.Deleted;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var mail in mails)
            db.Entry(mail).State = EntityState.Detached;
    }

    private async Task RemoveVanishedAsync(
        Guid folderId,
        uint uidValidity,
        IReadOnlyList<ReconciliationRow> chunk,
        IReadOnlyDictionary<uint, RemoteMessageFlags> flags,
        CancellationToken cancellationToken)
    {
        var vanished = chunk
            .Where(row => !flags.ContainsKey(row.Uid) && !row.HasPendingLocalOperation)
            .Select(row => row.Id)
            .ToList();
        if (vanished.Count == 0)
            return;

        var storagePaths = await db.Attachments
            .Where(attachment => vanished.Contains(attachment.MailId))
            .Select(attachment => attachment.StoragePath)
            .ToListAsync(cancellationToken);
        var mails = await db.Mails
            .Where(mail => mail.MailFolderId == folderId && mail.UidValidity == uidValidity && vanished.Contains(mail.Id))
            .ToListAsync(cancellationToken);
        db.Mails.RemoveRange(mails);
        await db.SaveChangesAsync(cancellationToken);
        await CleanupCreatedFilesAsync(storagePaths);
        logger.LogInformation("Removed {Count} messages that no longer exist on the server.", mails.Count);
    }

    private sealed record ReconciliationRow(
        Guid Id,
        uint Uid,
        bool IsRead,
        bool Answered,
        bool Flagged,
        bool Draft,
        bool Deleted,
        MailReconciliationState ReconciliationState,
        Guid? ExpectedMailFolderId)
    {
        public bool HasPendingLocalOperation =>
            ReconciliationState == MailReconciliationState.Pending || ExpectedMailFolderId is not null;

        public bool Differs(RemoteMessageFlags remote) =>
            IsRead != remote.Seen
            || Answered != remote.Answered
            || Flagged != remote.Flagged
            || Draft != remote.Draft
            || Deleted != remote.Deleted;
    }

    private async Task<Guid?> SyncOneAsync(
        Guid accountId,
        Guid folderId,
        SyncState state,
        IRemoteMailFolder remote,
        UniqueId uid,
        RemoteSummary? summary,
        HashSet<uint> committedUids,
        HashSet<uint> skippedUids,
        bool isNewMail,
        CancellationToken cancellationToken)
    {
        if (committedUids.Contains(uid.Id) || skippedUids.Contains(uid.Id))
        {
            await AdvanceCheckpointAsync(state, uid.Id, cancellationToken);
            return null;
        }

        var checkpointBefore = state.LastUid;
        var createdPaths = new List<string>();
        try
        {
            if (summary is null)
            {
                await SkipAndAdvanceAsync(state, folderId, uid.Id, "gone", "Message no longer exists on server.",
                    accountId, cancellationToken);
                return null;
            }

            if (summary.Size > (ulong)operationSettings.Current.Limits.MaxMessageBytes)
            {
                logger.LogWarning("Skipped an oversized message ({Size} bytes).", summary.Size);
                await SkipAndAdvanceAsync(state, folderId, uid.Id, "oversized", "Message exceeds MaxMessageBytes.",
                    accountId, cancellationToken);
                return null;
            }

            var message = await remote.GetMessageAsync(uid, cancellationToken);
            var incomingMessageId = MailFieldNormalizer.MessageId(message.MessageId);
            if (await reconciliations.ReconcileAsync(accountId, folderId, incomingMessageId, uid.Id, remote.UidValidity, cancellationToken))
            {
                state.LastUid = Math.Max(state.LastUid, uid.Id);
                await db.SaveChangesAsync(cancellationToken);
                return null;
            }
            var incoming = IncomingMailMapper.Map(
                message,
                uid.Id,
                remote.UidValidity,
                accountId,
                folderId,
                new IncomingFlags(summary.IsSeen, summary.IsAnswered, summary.IsFlagged, summary.IsDraft, summary.IsDeleted, summary.IsRecent),
                summary.InternalDate);
            var mail = new MailEntity
            {
                Id = Guid.NewGuid(),
                MailAccountId = incoming.MailAccountId,
                MailFolderId = incoming.MailFolderId,
                Uid = incoming.Uid,
                UidValidity = incoming.UidValidity,
                MessageId = incoming.MessageId,
                InReplyToMessageId = incoming.InReplyToMessageId,
                References = incoming.References,
                Subject = incoming.Subject,
                FromAddress = incoming.FromAddress,
                FromDisplayName = incoming.FromDisplayName,
                ToAddress = incoming.ToAddress,
                BodyHtml = incoming.BodyHtml,
                BodyText = incoming.BodyText,
                SentAt = incoming.SentAt,
                ReceivedAt = incoming.ReceivedAt,
                InternalDate = incoming.InternalDate,
                IsRead = incoming.IsRead,
                Answered = incoming.Answered,
                Flagged = incoming.Flagged,
                Draft = incoming.Draft,
                Deleted = incoming.Deleted,
                Recent = incoming.Recent
            };
            mail.Participants = IncomingMailMapper.ToEntityParticipants(mail.Id, incoming.Participants);
            mail.Headers = incoming.Headers
                .Select(header => new MailHeader
                {
                    Id = Guid.NewGuid(),
                    MailId = mail.Id,
                    Name = header.Name,
                    Value = header.Value
                })
                .ToList();
            var messageAttachmentBytes = 0L;

            foreach (var attachment in incoming.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = operationSettings.Current.Limits.MaxMessageAttachmentBytes - messageAttachmentBytes;
                if (remaining <= 0)
                {
                    logger.LogWarning("Skipped an oversized attachment.");
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
                        Math.Min(operationSettings.Current.Limits.MaxAttachmentBytes, remaining),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AttachmentLimitExceededException)
                {
                    logger.LogWarning("Skipped an oversized attachment.");
                    continue;
                }

                createdPaths.Add(stored.RelativePath);
                mail.Attachments.Add(new Attachment
                {
                    Id = attachmentId,
                    MailAccountId = accountId,
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
            mail.RulePending = isNewMail;
            mail.NotificationPending = isNewMail;

            db.Mails.Add(mail);
            state.LastUid = Math.Max(state.LastUid, uid.Id);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                Detach(mail);
                throw;
            }

            // The committed row owns its attachment files now; no later failure may delete them.
            createdPaths.Clear();
            await AssignConversationAsync(mail.Id, cancellationToken);
            var importedId = mail.Id;
            Detach(mail);
            return importedId;
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
            logger.LogWarning(ex, "Skipped a message because it could not be processed.");
            try
            {
                await SkipAndAdvanceAsync(state, folderId, uid.Id, "failed", ex.GetType().Name,
                    accountId, cancellationToken);
            }
            catch
            {
                state.LastUid = checkpointBefore;
                throw;
            }

            return null;
        }
        catch (Exception ex)
        {
            state.LastUid = checkpointBefore;
            await CleanupCreatedFilesAsync(createdPaths);
            logger.LogWarning(ex, "Transient message sync failure. Retrying on the next poll.");
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
                MailAccountId = accountId,
                MailFolderId = folderId,
                Uid = uid,
                Reason = MailFieldNormalizer.Truncate($"{kind}: {detail}", 500),
                SkippedAt = DateTime.UtcNow
            });
        }

        if (uid > state.LastUid)
            state.LastUid = uid;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Marked a message as skipped ({Kind}).", kind);
    }

    /// <summary>
    /// Threading is a derived view of an already committed message: a failure here must not undo the import, so it
    /// is logged and the message stays unthreaded instead of losing its attachments or being retried as new mail.
    /// </summary>
    private async Task AssignConversationAsync(Guid mailId, CancellationToken cancellationToken)
    {
        try
        {
            await conversations.AssignAsync(mailId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            foreach (var entry in db.ChangeTracker.Entries()
                .Where(entry => entry.Entity is MailEntity or Conversation && entry.State != EntityState.Unchanged)
                .ToList())
                entry.State = EntityState.Detached;
            logger.LogWarning(ex, "Conversation assignment failed for an imported message.");
        }
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

    private void Detach(MailEntity mail)
    {
        foreach (var attachment in mail.Attachments)
            db.Entry(attachment).State = EntityState.Detached;
        db.Entry(mail).State = EntityState.Detached;
    }
}
