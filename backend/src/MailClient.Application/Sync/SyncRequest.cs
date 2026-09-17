using MailClient.Domain.Enums;

namespace MailClient.Application.Sync;

public enum SyncPriority
{
    UserRequested = 0,
    InitialOrReconciliation = 1,
    PeriodicInbox = 2,
    PeriodicOtherFolder = 3
}

public enum SyncOrigin
{
    Initial,
    UserRequested,
    Reconciliation,
    Periodic
}

public readonly record struct SyncRequest(Guid AccountId, Guid? FolderId)
{
    public static SyncRequest Account(Guid accountId) => new(accountId, null);
    public static SyncRequest Folder(Guid accountId, Guid folderId) => new(accountId, folderId);
}

public sealed record ScheduledSyncRequest(
    SyncRequest Request,
    SyncPriority Priority,
    SyncOrigin Origin,
    bool IsInbox,
    long Sequence);

public interface ISyncExecutor
{
    Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken);
}

public static class SyncScheduling
{
    public static ScheduledSyncRequest ForInitialAccount(Guid accountId, long sequence) =>
        new(SyncRequest.Account(accountId), SyncPriority.InitialOrReconciliation, SyncOrigin.Initial, IsInbox: false, sequence);

    public static ScheduledSyncRequest ForUserFolder(Guid accountId, Guid folderId, long sequence) =>
        new(SyncRequest.Folder(accountId, folderId), SyncPriority.UserRequested, SyncOrigin.UserRequested, IsInbox: false, sequence);

    public static ScheduledSyncRequest ForUserAccount(Guid accountId, long sequence) =>
        new(SyncRequest.Account(accountId), SyncPriority.UserRequested, SyncOrigin.UserRequested, IsInbox: false, sequence);

    public static ScheduledSyncRequest ForReconciliationFolder(Guid accountId, Guid folderId, long sequence) =>
        new(SyncRequest.Folder(accountId, folderId), SyncPriority.InitialOrReconciliation, SyncOrigin.Reconciliation, IsInbox: false, sequence);

    public static ScheduledSyncRequest ForPeriodicFolder(Guid accountId, Guid folderId, MailFolderType folderType, long sequence) =>
        new(
            SyncRequest.Folder(accountId, folderId),
            folderType == MailFolderType.Inbox ? SyncPriority.PeriodicInbox : SyncPriority.PeriodicOtherFolder,
            SyncOrigin.Periodic,
            folderType == MailFolderType.Inbox,
            sequence);
}
