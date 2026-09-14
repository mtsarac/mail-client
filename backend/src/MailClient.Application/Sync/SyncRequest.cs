namespace MailClient.Application.Sync;

public readonly record struct SyncRequest(Guid AccountId, Guid? FolderId)
{
    public static SyncRequest Account(Guid accountId) => new(accountId, null);
    public static SyncRequest Folder(Guid accountId, Guid folderId) => new(accountId, folderId);
}

public interface ISyncExecutor
{
    Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken);
}
