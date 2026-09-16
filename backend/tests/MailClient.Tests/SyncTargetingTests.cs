using MailClient.Application.Sync;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class SyncTargetingTests
{
    [Fact]
    public async Task Queue_DeduplicatesPendingAccountRequests()
    {
        var queue = new InitialSyncQueue();
        var accountId = Guid.NewGuid();

        await queue.EnqueueAsync(SyncRequest.Account(accountId), CancellationToken.None);
        await queue.EnqueueAsync(SyncRequest.Account(accountId), CancellationToken.None);

        var received = await ReadAsync(queue, 1);

        Assert.Equal([SyncRequest.Account(accountId)], received);
    }

    [Fact]
    public async Task Queue_KeepsDistinctAccountAndFolderRequests()
    {
        var queue = new InitialSyncQueue();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var folderX = Guid.NewGuid();
        var folderY = Guid.NewGuid();

        await queue.EnqueueAsync(SyncRequest.Account(accountA), CancellationToken.None);
        await queue.EnqueueAsync(SyncRequest.Account(accountB), CancellationToken.None);
        await queue.EnqueueAsync(SyncRequest.Folder(accountA, folderX), CancellationToken.None);
        await queue.EnqueueAsync(SyncRequest.Folder(accountA, folderY), CancellationToken.None);

        var received = await ReadAsync(queue, 4);

        Assert.Equal(4, received.Count);
        Assert.Contains(SyncRequest.Account(accountA), received);
        Assert.Contains(SyncRequest.Account(accountB), received);
        Assert.Contains(SyncRequest.Folder(accountA, folderX), received);
        Assert.Contains(SyncRequest.Folder(accountA, folderY), received);
    }

    [Fact]
    public async Task Queue_AllowsReenqueueAfterDequeue()
    {
        var queue = new InitialSyncQueue();
        var accountId = Guid.NewGuid();

        await queue.EnqueueAsync(SyncRequest.Account(accountId), CancellationToken.None);
        Assert.Single(await ReadAsync(queue, 1));
        await queue.EnqueueAsync(SyncRequest.Account(accountId), CancellationToken.None);

        Assert.Single(await ReadAsync(queue, 1));
    }

    [Fact]
    public async Task Worker_AccountRequest_SyncsOnlyThatAccount()
    {
        var executor = new RecordingSyncExecutor();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        using var worker = CreateWorker(executor, out var queue, out var stopping);
        await worker.StartAsync(stopping.Token);

        await queue.EnqueueAsync(SyncRequest.Account(accountA), CancellationToken.None);
        await WaitUntilAsync(() => executor.AccountCalls.Count == 1);

        Assert.Equal([accountA], executor.AccountCalls);
        Assert.DoesNotContain(accountB, executor.AccountCalls);
        Assert.Empty(executor.FolderCalls);
        await stopping.CancelAsync();
    }

    [Fact]
    public async Task Worker_FolderRequest_SyncsOnlyThatFolder()
    {
        var executor = new RecordingSyncExecutor();
        var accountId = Guid.NewGuid();
        var folderX = Guid.NewGuid();
        var folderY = Guid.NewGuid();
        using var worker = CreateWorker(executor, out var queue, out var stopping);
        await worker.StartAsync(stopping.Token);

        await queue.EnqueueAsync(SyncRequest.Folder(accountId, folderX), CancellationToken.None);
        await WaitUntilAsync(() => executor.FolderCalls.Count == 1);

        Assert.Equal([(accountId, folderX)], executor.FolderCalls);
        Assert.DoesNotContain((accountId, folderY), executor.FolderCalls);
        Assert.Empty(executor.AccountCalls);
        await stopping.CancelAsync();
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

    private static InitialSyncWorker CreateWorker(
        ISyncExecutor executor,
        out InitialSyncQueue queue,
        out CancellationTokenSource stopping)
    {
        queue = new InitialSyncQueue();
        var services = new ServiceCollection();
        services.AddSingleton(queue);
        services.AddSingleton(executor);
        var provider = services.BuildServiceProvider();
        stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return new InitialSyncWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<InitialSyncWorker>.Instance);
    }

    private static async Task<List<SyncRequest>> ReadAsync(InitialSyncQueue queue, int count)
    {
        var received = new List<SyncRequest>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var request in queue.ReadAllAsync(timeout.Token))
            {
                received.Add(request);
                if (received.Count == count) break;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return received;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition(), "The queued request was not dispatched in time.");
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class RecordingSyncExecutor : ISyncExecutor
    {
        public List<Guid> AccountCalls { get; } = [];
        public List<(Guid AccountId, Guid FolderId)> FolderCalls { get; } = [];

        public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken)
        {
            AccountCalls.Add(accountId);
            return Task.CompletedTask;
        }

        public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            FolderCalls.Add((accountId, folderId));
            return Task.CompletedTask;
        }
    }
}
