using MailClient.Infrastructure.Sync;
using MailClient.Application.Sync;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class SyncTargetingTests
{
    [Fact]
    public void Queue_UserWorkJumpsAheadOfWaitingPeriodicWork()
    {
        var queue = new SyncScheduleQueue(10);
        var accountId = Guid.NewGuid();
        var inboxId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        queue.Enqueue(SyncScheduling.ForPeriodicFolder(accountId, otherId, MailFolderType.Custom, queue.NextSequence()));
        queue.Enqueue(SyncScheduling.ForPeriodicFolder(accountId, inboxId, MailFolderType.Inbox, queue.NextSequence()));
        queue.Enqueue(SyncScheduling.ForUserFolder(accountId, userId, queue.NextSequence()));

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(SyncPriority.UserRequested, first.Priority);
        Assert.Equal(userId, first.Request.FolderId);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(SyncPriority.PeriodicInbox, second.Priority);
        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal(SyncPriority.PeriodicOtherFolder, third.Priority);
    }

    [Fact]
    public void Queue_InitialAndReconciliationOutrankPeriodicNonInbox()
    {
        var queue = new SyncScheduleQueue(10);
        var accountId = Guid.NewGuid();

        queue.Enqueue(SyncScheduling.ForPeriodicFolder(accountId, Guid.NewGuid(), MailFolderType.Custom, queue.NextSequence()));
        queue.Enqueue(SyncScheduling.ForInitialAccount(accountId, queue.NextSequence()));
        queue.Enqueue(SyncScheduling.ForReconciliationFolder(accountId, Guid.NewGuid(), queue.NextSequence()));

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(SyncOrigin.Initial, first.Origin);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(SyncOrigin.Reconciliation, second.Origin);
        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal(SyncOrigin.Periodic, third.Origin);
    }

    [Fact]
    public void Queue_CoalescesEquivalentPeriodicWork()
    {
        var queue = new SyncScheduleQueue(10);
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        queue.Enqueue(SyncScheduling.ForPeriodicFolder(accountId, folderId, MailFolderType.Custom, queue.NextSequence()));
        queue.Enqueue(SyncScheduling.ForPeriodicFolder(accountId, folderId, MailFolderType.Custom, queue.NextSequence()));

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Queue_PromotesWaitingWorkWhenHigherPriorityArrives()
    {
        var queue = new SyncScheduleQueue(10);
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        queue.Enqueue(SyncScheduling.ForPeriodicFolder(accountId, folderId, MailFolderType.Custom, queue.NextSequence()));
        queue.Enqueue(SyncScheduling.ForUserFolder(accountId, folderId, queue.NextSequence()));

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(SyncPriority.UserRequested, first.Priority);
    }

    [Fact]
    public void Queue_DropsBackgroundWorkWhenFull_UserWorkThrowsOnlyWhenNothingSheddable()
    {
        var queue = new SyncScheduleQueue(2);
        var accountId = Guid.NewGuid();

        queue.Enqueue(SyncScheduling.ForPeriodicFolder(accountId, Guid.NewGuid(), MailFolderType.Custom, queue.NextSequence()));
        queue.Enqueue(SyncScheduling.ForPeriodicFolder(accountId, Guid.NewGuid(), MailFolderType.Custom, queue.NextSequence()));
        queue.Enqueue(SyncScheduling.ForUserFolder(accountId, Guid.NewGuid(), queue.NextSequence()));

        Assert.Equal(2, queue.Count);

        var saturated = new SyncScheduleQueue(1);
        saturated.Enqueue(SyncScheduling.ForUserFolder(accountId, Guid.NewGuid(), queue.NextSequence()));
        Assert.Throws<SyncQueueFullException>(() =>
            saturated.Enqueue(SyncScheduling.ForUserFolder(accountId, Guid.NewGuid(), saturated.NextSequence())));
    }

    [Fact]
    public void Queue_WhenFull_ShedsLeastImportantBackgroundWorkFirst()
    {
        var queue = new SyncScheduleQueue(2);
        var accountId = Guid.NewGuid();
        var inbox = SyncScheduling.ForPeriodicFolder(accountId, Guid.NewGuid(), MailFolderType.Inbox, queue.NextSequence());
        var other = SyncScheduling.ForPeriodicFolder(accountId, Guid.NewGuid(), MailFolderType.Custom, queue.NextSequence());
        queue.Enqueue(inbox);
        queue.Enqueue(other);

        Assert.Equal(SyncEnqueueResult.EnqueuedAfterShedding, queue.Enqueue(SyncScheduling.ForUserFolder(accountId, Guid.NewGuid(), queue.NextSequence())));

        Assert.True(queue.Contains(inbox.Request));
        Assert.False(queue.Contains(other.Request));
    }

    [Fact]
    public async Task InlineFolderSync_WhenAccountSyncIsRunning_QueuesInsteadOfSyncingConcurrently()
    {
        var executor = new CountingExecutor();
        var scheduler = new FakeSyncScheduler();
        var inline = new InlineFolderSync(new StubSyncLockProvider(SyncLockStatus.Contended), executor, scheduler);
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        Assert.False(await inline.TrySyncNowAsync(accountId, folderId, CancellationToken.None));

        Assert.Equal(0, executor.Calls);
        Assert.Equal((accountId, (Guid?)folderId, SyncOrigin.UserRequested), Assert.Single(scheduler.Scheduled));
    }

    private sealed class CountingExecutor : ISyncExecutor
    {
        public int Calls { get; private set; }
        public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task OwnsFolderAsync_RejectsFolderFromAnotherAccount()
    {
        await using var db = CreateDb();
        var owner = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new Domain.Entities.MailAccount
        {
            Id = owner,
            EmailAddress = "owner@example.test",
            NormalizedEmailAddress = "OWNER@EXAMPLE.TEST"
        });
        db.MailFolders.Add(new Domain.Entities.MailFolder
        {
            Id = folderId,
            MailAccountId = owner,
            Name = "INBOX",
            FullName = "INBOX",
            IsSyncEnabled = true,
            IsAvailable = true
        });
        await db.SaveChangesAsync();
        var service = new MailFolderAccessService(db);

        Assert.True(await service.OwnsFolderAsync(owner, folderId, CancellationToken.None));
        Assert.False(await service.OwnsFolderAsync(Guid.NewGuid(), folderId, CancellationToken.None));
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
