using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Services;

public sealed class NewMailSyncExecutor(
    MailFolderSyncService sync,
    NewMailNotifier notifier) : ISyncExecutor
{
    public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
    {
        await sync.SyncFolderAsync(accountId, folderId, cancellationToken);
        await notifier.NotifyPendingAsync(accountId, cancellationToken);
    }
}
