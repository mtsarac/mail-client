using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Services;

public sealed class RuleEvaluatingSyncExecutor(MailFolderSyncService sync, MailRuleEvaluator rules) : ISyncExecutor
{
    public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
    {
        await sync.SyncFolderAsync(accountId, folderId, cancellationToken);
        await rules.EvaluatePendingAsync(accountId, folderId, cancellationToken);
    }
}
