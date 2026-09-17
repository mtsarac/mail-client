namespace MailClient.Application.Sync;

public interface ISyncScheduler
{
    ValueTask ScheduleAccountAsync(Guid accountId, SyncOrigin origin, CancellationToken cancellationToken);
    ValueTask ScheduleFolderAsync(Guid accountId, Guid folderId, SyncOrigin origin, CancellationToken cancellationToken);
}
