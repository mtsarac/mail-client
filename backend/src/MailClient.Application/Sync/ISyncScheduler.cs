namespace MailClient.Application.Sync;

public interface ISyncScheduler
{
    ValueTask ScheduleAccountAsync(Guid accountId, SyncOrigin origin, CancellationToken cancellationToken);
    ValueTask ScheduleFolderAsync(Guid accountId, Guid folderId, SyncOrigin origin, CancellationToken cancellationToken);
    ValueTask<Guid> ScheduleUserFolderJobAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken);
    SyncJobStatus? GetJobStatus(Guid accountId, Guid jobId);
}

public sealed record SyncJobStatus(Guid JobId, string Status, string? ErrorCode);
