using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Sync;

/// <summary>
/// Request-path folder sync (after a send or draft append) that respects the same account lock as the background
/// coordinator, so two syncs never write one folder's checkpoint concurrently.
/// </summary>
public sealed class InlineFolderSync(ISyncLockProvider locks, ISyncExecutor executor, ISyncScheduler scheduler)
{
    /// <summary>
    /// Syncs the folder now when no other sync owns the account; otherwise queues a user-priority sync instead.
    /// Returns whether the folder was synced within this call.
    /// </summary>
    public async Task<bool> TrySyncNowAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
    {
        await using var accountLock = await locks.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, cancellationToken);
        if (!accountLock.IsAcquired)
        {
            await scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.UserRequested, cancellationToken);
            return false;
        }

        await executor.SyncFolderAsync(accountId, folderId, cancellationToken);
        return true;
    }
}
