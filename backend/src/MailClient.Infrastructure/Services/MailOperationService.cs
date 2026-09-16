using System.Security.Cryptography;
using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailKit;
using MailFolderEntity = MailClient.Domain.Entities.MailFolder;

namespace MailClient.Infrastructure.Services;

public sealed class MailOperationService(
    AppDbContext db,
    IMailFolderClient folders,
    MailReadService readService,
    AuditLogger audit,
    InitialSyncQueue syncQueue,
    ILogger<MailOperationService> logger) : IMailOperationService
{
    public async Task<MailOperationResult> ExecuteAsync(Guid accountId, MailOperationRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        var mail = await db.Mails.Include(item => item.MailAccount).Include(item => item.MailFolder)
            .SingleOrDefaultAsync(item => item.Id == request.MailId && item.MailAccountId == accountId, cancellationToken);
        if (mail?.MailAccount is null || mail.MailFolder is null)
            return new(false, MailOperationError.NotFound);
        if (mail.MailAccount.Status == MailAccountStatus.NeedsReauthentication)
            return new(false, MailOperationError.NeedsReauthentication);
        if (request.Kind is MailOperationKind.Read or MailOperationKind.Unread)
        {
            var outcome = await readService.SetReadAsync(accountId, request.MailId, request.Kind == MailOperationKind.Read, correlationId, cancellationToken);
            return outcome switch
            {
                { Found: false } => new(false, MailOperationError.NotFound),
                { Conflict: true } => new(false, MailOperationError.Conflict),
                { ProviderError: true } => new(false, MailOperationError.ProviderUnavailable),
                _ => new(true)
            };
        }
        var auditKind = request.Kind;
        if (request.Kind == MailOperationKind.Restore)
        {
            if (mail.PreviousMailFolderId is not { } previousFolderId)
                return new(false, MailOperationError.NotSupported);
            var previousFolder = await db.MailFolders.SingleOrDefaultAsync(folder => folder.Id == previousFolderId && folder.MailAccountId == accountId && folder.IsAvailable, cancellationToken);
            if (previousFolder is null)
                return new(false, MailOperationError.NotSupported);
            request = request with { Kind = MailOperationKind.Move, DestinationFolderId = previousFolderId };
        }
        if (request.Kind == MailOperationKind.Star && mail.Flagged || request.Kind == MailOperationKind.Unstar && !mail.Flagged)
            return new(true);

        var target = await ResolveDestinationAsync(accountId, request, cancellationToken);
        if (request.Kind is MailOperationKind.Move or MailOperationKind.Copy or MailOperationKind.Trash or MailOperationKind.Archive or MailOperationKind.Spam or MailOperationKind.NotSpam
            && target is null)
            return new(false, MailOperationError.FolderNotFound);

        try
        {
            var remoteResult = await folders.UseFolderAsync(mail.MailAccount, mail.MailFolder.FullName, true, async (remote, ct) =>
            {
                if (remote.UidValidity != mail.UidValidity)
                    throw new MailOperationConflictException();
                var uid = new UniqueId(mail.Uid);
                return request.Kind switch
                {
                    MailOperationKind.Read => await SetSeen(remote, uid, true, ct),
                    MailOperationKind.Unread => await SetSeen(remote, uid, false, ct),
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
                    await syncQueue.EnqueueAsync(SyncRequest.Folder(accountId, target.Id), cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
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
            }
            else if (request.Kind is MailOperationKind.Read or MailOperationKind.Unread)
                mail.IsRead = request.Kind == MailOperationKind.Read;
            else if (request.Kind is MailOperationKind.Star or MailOperationKind.Unstar)
                mail.Flagged = request.Kind == MailOperationKind.Star;
            else if (request.Kind == MailOperationKind.Copy)
                return await AuditSuccess(accountId, auditKind, mail.Id, correlationId, cancellationToken, new(true));

            await db.SaveChangesAsync(cancellationToken);
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

    private static async Task<RemoteMoveResult?> SetSeen(IRemoteMailFolder remote, UniqueId uid, bool value, CancellationToken cancellationToken)
    {
        await remote.SetSeenAsync(uid, value, cancellationToken);
        return null;
    }

    private static async Task<RemoteMoveResult?> SetFlagged(IRemoteMailFolder remote, UniqueId uid, bool value, CancellationToken cancellationToken)
    {
        await remote.SetFlaggedAsync(uid, value, cancellationToken);
        return null;
    }

    private async Task<MailOperationResult> AuditSuccess(Guid accountId, MailOperationKind kind, Guid mailId, string? correlationId, CancellationToken cancellationToken, MailOperationResult result)
    {
        await audit.WriteAsync(accountId, $"mail.{kind.ToString().ToLowerInvariant()}", "Mail", mailId.ToString(), null, correlationId, cancellationToken);
        return result;
    }

    private sealed class MailOperationConflictException : Exception;
}
