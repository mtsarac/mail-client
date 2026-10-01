using System.Security.Cryptography;
using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailKit;
using MailFolderEntity = MailClient.Domain.Entities.MailFolder;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Infrastructure.Services;

public sealed class MailOperationService(
    AppDbContext db,
    IMailFolderClient folders,
    MailReadService readService,
    AuditLogger audit,
    ISyncScheduler scheduler,
    ILogger<MailOperationService> logger,
    IPushNotificationService push,
    IFileStorage storage,
    InlineFolderSync inlineSync) : IMailOperationService
{
    public async Task<MailOperationResult> ExecuteAsync(Guid accountId, MailOperationRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        var mail = await db.Mails.Include(item => item.MailAccount).Include(item => item.MailFolder)
            .SingleOrDefaultAsync(item => item.Id == request.MailId && item.MailAccountId == accountId, cancellationToken);
        if (mail?.MailAccount is null || mail.MailFolder is null)
            return new(false, MailOperationError.NotFound);
        if (mail.MailAccount.Status == MailAccountStatus.NeedsReauthentication)
            return new(false, MailOperationError.NeedsReauthentication);
        if (mail.ReconciliationState == MailReconciliationState.Pending || mail.ExpectedMailFolderId is not null)
        {
            if (mail.ExpectedMailFolderId is { } expectedFolderId)
            {
                try
                {
                    await inlineSync.TrySyncNowAsync(accountId, expectedFolderId, cancellationToken);
                    await db.Entry(mail).ReloadAsync(cancellationToken);
                    if (db.Entry(mail).State == EntityState.Detached)
                        return new(false, MailOperationError.NotFound);
                    mail.MailFolder = await db.MailFolders.SingleAsync(
                        folder => folder.Id == mail.MailFolderId && folder.MailAccountId == accountId,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is MailConnectionException or CryptographicException)
                {
                    logger.LogWarning(exception, "Pending move reconciliation failed for mail {MailId}.", mail.Id);
                    await scheduler.ScheduleFolderAsync(accountId, expectedFolderId, SyncOrigin.Reconciliation, cancellationToken);
                }
            }
            if (mail.ReconciliationState == MailReconciliationState.Pending || mail.ExpectedMailFolderId is not null)
                return new(false, MailOperationError.ReconciliationPending, true);
        }
        if (request.Kind is MailOperationKind.Read or MailOperationKind.Unread)
        {
            var outcome = await readService.SetReadAsync(accountId, request.MailId, request.Kind == MailOperationKind.Read, correlationId, cancellationToken);
            if (!outcome.Found)
                return new(false, MailOperationError.NotFound);
            if (outcome.Conflict)
                return new(false, MailOperationError.Conflict);
            if (outcome.ProviderError)
                return new(false, MailOperationError.ProviderUnavailable);
            if (outcome.Applied)
                await NotifyStateChangedAsync(accountId, mail, mail.MailFolderId, OperationName(request.Kind), cancellationToken);
            return new(true);
        }
        var auditKind = request.Kind;
        if (request.Kind == MailOperationKind.Restore)
        {
            Guid restoreFolderId;
            if (mail.PreviousMailFolderId is { } previousFolderId)
            {
                var previousFolder = await db.MailFolders.SingleOrDefaultAsync(folder => folder.Id == previousFolderId && folder.MailAccountId == accountId && folder.IsAvailable, cancellationToken);
                if (previousFolder is null)
                    return new(false, MailOperationError.NotSupported);
                restoreFolderId = previousFolder.Id;
            }
            else
            {
                // Mails that reached Trash/Junk outside this app (another client, server rule) have no recorded origin.
                if (mail.MailFolder.FolderType is not (MailFolderType.Trash or MailFolderType.Junk))
                    return new(false, MailOperationError.NotSupported);
                var inbox = await ResolveDestinationAsync(accountId, new MailOperationRequest(mail.Id, MailOperationKind.NotSpam), cancellationToken);
                if (inbox is null)
                    return new(false, MailOperationError.FolderNotFound);
                restoreFolderId = inbox.Id;
            }
            request = request with { Kind = MailOperationKind.Move, DestinationFolderId = restoreFolderId };
        }
        if (request.Kind == MailOperationKind.Star && mail.Flagged || request.Kind == MailOperationKind.Unstar && !mail.Flagged)
            return new(true);

        var target = await ResolveDestinationAsync(accountId, request, cancellationToken);
        if (request.Kind is MailOperationKind.Move or MailOperationKind.Copy or MailOperationKind.Trash or MailOperationKind.Archive or MailOperationKind.Spam or MailOperationKind.NotSpam
            && target is null)
            return new(false, MailOperationError.FolderNotFound);
        var isMove = request.Kind is MailOperationKind.Move or MailOperationKind.Trash or MailOperationKind.Archive or MailOperationKind.Spam or MailOperationKind.NotSpam;
        string? fingerprint = null;
        if (isMove && target!.Id == mail.MailFolderId)
            return await AuditSuccess(accountId, auditKind, mail.Id, correlationId, cancellationToken, new(true));

        try
        {
            if (request.Kind == MailOperationKind.Delete)
                return await DeletePermanentlyAsync(accountId, mail, correlationId, cancellationToken);
            var remoteResult = await folders.UseFolderAsync(mail.MailAccount, mail.MailFolder.FullName, true, async (remote, ct) =>
            {
                if (remote.UidValidity != mail.UidValidity)
                    throw new MailOperationConflictException();
                var uid = new UniqueId(mail.Uid);
                if (isMove && string.IsNullOrWhiteSpace(mail.MessageId))
                    fingerprint = await MailReconciliationService.FingerprintAsync(await remote.GetMessageAsync(uid, ct), ct);
                return request.Kind switch
                {
                    MailOperationKind.Star => await SetFlagged(remote, uid, true, ct),
                    MailOperationKind.Unstar => await SetFlagged(remote, uid, false, ct),
                    MailOperationKind.Move or MailOperationKind.Trash or MailOperationKind.Archive or MailOperationKind.Spam or MailOperationKind.NotSpam
                        => await remote.MoveAsync(uid, target!.FullName, ct),
                    MailOperationKind.Copy => await remote.CopyAsync(uid, target!.FullName, ct),
                    _ => null
                };
            }, cancellationToken);

            if (request.Kind is MailOperationKind.Move or MailOperationKind.Trash or MailOperationKind.Archive or MailOperationKind.Spam or MailOperationKind.NotSpam)
            {
                if (remoteResult is not RemoteMoveResult moveResult)
                    return new(false, MailOperationError.MoveFailed, true);
                if (moveResult.DestinationUid is not { } destinationUid)
                {
                    if (auditKind is MailOperationKind.Trash or MailOperationKind.Spam)
                        mail.PreviousMailFolderId = mail.MailFolderId;
                    mail.ExpectedMailFolderId = target!.Id;
                    mail.ReconciliationState = MailReconciliationState.Pending;
                    mail.IsRestoreReconciliation = auditKind == MailOperationKind.Restore;
                    mail.ReconciliationFingerprint = fingerprint;
                    // Commit first: a separate scheduler scope may immediately reconcile.
                    await db.SaveChangesAsync(cancellationToken);
                    await scheduler.ScheduleFolderAsync(accountId, target!.Id, SyncOrigin.Reconciliation, cancellationToken);
                    await NotifyStateChangedAsync(accountId, mail, target.Id, OperationName(auditKind), cancellationToken);
                    return await AuditSuccess(accountId, auditKind, mail.Id, correlationId, cancellationToken, new(true, MailOperationError.None, true));
                }
                mail.PreviousMailFolderId = auditKind is MailOperationKind.Restore
                    ? null
                    : auditKind is MailOperationKind.Trash or MailOperationKind.Spam ? mail.MailFolderId : mail.PreviousMailFolderId;
                mail.MailFolderId = target!.Id;
                mail.Uid = destinationUid.Id;
                mail.UidValidity = moveResult.DestinationUidValidity;
                mail.ReconciliationState = MailReconciliationState.None;
                mail.ExpectedMailFolderId = null;
                mail.IsRestoreReconciliation = false;
                mail.ReconciliationFingerprint = null;
            }
            else if (request.Kind is MailOperationKind.Star or MailOperationKind.Unstar)
                mail.Flagged = request.Kind == MailOperationKind.Star;
            else if (request.Kind == MailOperationKind.Copy)
            {
                await NotifyStateChangedAsync(accountId, mail, target!.Id, OperationName(auditKind), cancellationToken);
                return await AuditSuccess(accountId, auditKind, mail.Id, correlationId, cancellationToken, new(true));
            }

            await db.SaveChangesAsync(cancellationToken);
            await NotifyStateChangedAsync(accountId, mail, target?.Id ?? mail.MailFolderId, OperationName(auditKind), cancellationToken);
            return await AuditSuccess(accountId, auditKind, mail.Id, correlationId, cancellationToken, new(true));
        }
        catch (MailOperationConflictException)
        {
            return new(false, MailOperationError.Conflict);
        }
        catch (Exception exception) when (exception is MailConnectionException or CryptographicException)
        {
            logger.LogWarning(exception, "Mail operation failed for mail {MailId}.", request.MailId);
            return new(false, MailOperationError.ProviderUnavailable);
        }
    }

    public async Task<BulkMailOperationResult> ExecuteBulkAsync(Guid accountId, IReadOnlyList<Guid> mailIds, MailOperationKind kind, Guid? destinationFolderId, string? correlationId, CancellationToken cancellationToken)
    {
        var results = new List<BulkMailOperationItemResult>(mailIds.Count);
        foreach (var mailId in mailIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteAsync(accountId, new MailOperationRequest(mailId, kind, destinationFolderId), correlationId, cancellationToken);
            results.Add(new BulkMailOperationItemResult(mailId, result.Success, result.Error, result.ReconciliationPending));
        }

        return new BulkMailOperationResult(results);
    }

    private async Task<MailFolderEntity?> ResolveDestinationAsync(Guid accountId, MailOperationRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind is MailOperationKind.Move or MailOperationKind.Copy)
            return request.DestinationFolderId is { } folderId
                ? await db.MailFolders.SingleOrDefaultAsync(folder => folder.Id == folderId && folder.MailAccountId == accountId, cancellationToken)
                : null;
        var type = request.Kind switch
        {
            MailOperationKind.Trash => MailFolderType.Trash,
            MailOperationKind.Archive => MailFolderType.Archive,
            MailOperationKind.Spam => MailFolderType.Junk,
            MailOperationKind.NotSpam => MailFolderType.Inbox,
            _ => MailFolderType.Unknown
        };
        return type == MailFolderType.Unknown ? null : await db.MailFolders
            .Where(folder => folder.MailAccountId == accountId && folder.FolderType == type && folder.IsAvailable)
            .OrderBy(folder => folder.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Expunges only this UID on the server, then drops the cached row (participants, headers and
    /// attachment rows cascade) and its stored attachment files.</summary>
    private async Task<MailOperationResult> DeletePermanentlyAsync(Guid accountId, MailEntity mail, string? correlationId, CancellationToken cancellationToken)
    {
        if (mail.MailFolder!.FolderType is not (MailFolderType.Trash or MailFolderType.Junk))
            return new(false, MailOperationError.NotSupported);
        var expunged = await folders.UseFolderAsync(mail.MailAccount!, mail.MailFolder.FullName, true, async (remote, ct) =>
        {
            if (remote.UidValidity != mail.UidValidity)
                throw new MailOperationConflictException();
            return await remote.ExpungeAsync(new UniqueId(mail.Uid), ct);
        }, cancellationToken);
        if (!expunged)
            return new(false, MailOperationError.DeleteFailed);

        var storagePaths = await db.Attachments
            .Where(attachment => attachment.MailId == mail.Id)
            .Select(attachment => attachment.StoragePath)
            .ToListAsync(cancellationToken);
        db.Mails.Remove(mail);
        await db.SaveChangesAsync(cancellationToken);
        // The server copy is already gone, so file cleanup must not be cut short by the request being cancelled.
        foreach (var path in storagePaths)
        {
            try
            {
                await storage.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove an attachment file of a permanently deleted mail.");
            }
        }

        await NotifyStateChangedAsync(accountId, mail, mail.MailFolderId, OperationName(MailOperationKind.Delete), cancellationToken);
        return await AuditSuccess(accountId, MailOperationKind.Delete, mail.Id, correlationId, cancellationToken, new(true));
    }

    private static async Task<RemoteMoveResult?> SetFlagged(IRemoteMailFolder remote, UniqueId uid, bool value, CancellationToken cancellationToken)
    {
        await remote.SetFlaggedAsync(uid, value, cancellationToken);
        return null;
    }

    private async Task NotifyStateChangedAsync(Guid accountId, MailEntity mail, Guid folderId, string operation, CancellationToken cancellationToken)
    {
        try
        {
            await push.NotifyAsync(
                new PushEvent(PushEventType.MailStateChanged, accountId, mail.Id, mail.ConversationId, folderId, operation),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "State-change push failed for mail {MailId}. Operation result is unaffected.", mail.Id);
        }
    }

    private static string OperationName(MailOperationKind kind) => kind switch
    {
        MailOperationKind.Read => "read",
        MailOperationKind.Unread => "unread",
        MailOperationKind.Star => "star",
        MailOperationKind.Unstar => "unstar",
        MailOperationKind.Move => "move",
        MailOperationKind.Copy => "copy",
        MailOperationKind.Trash => "trash",
        MailOperationKind.Restore => "restore",
        MailOperationKind.Archive => "archive",
        MailOperationKind.Spam => "spam",
        MailOperationKind.NotSpam => "not_spam",
        MailOperationKind.Delete => "delete",
        _ => kind.ToString().ToLowerInvariant()
    };

    private async Task<MailOperationResult> AuditSuccess(Guid accountId, MailOperationKind kind, Guid mailId, string? correlationId, CancellationToken cancellationToken, MailOperationResult result)
    {
        await audit.WriteAsync(accountId, $"mail.{kind.ToString().ToLowerInvariant()}", "Mail", mailId.ToString(), null, correlationId, cancellationToken);
        return result;
    }

    private sealed class MailOperationConflictException : Exception;
}
