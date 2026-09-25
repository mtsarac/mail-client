using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Services;

public sealed class NewMailSyncExecutor(MailFolderSyncService sync, MailRuleEvaluator rules, NewMailNotifier notifier) : ISyncExecutor
{
    public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
    {
        await sync.SyncFolderAsync(accountId, folderId, cancellationToken);
        await rules.EvaluatePendingAsync(accountId, folderId, cancellationToken);
        await notifier.NotifyPendingAsync(accountId, cancellationToken);
    }
}
