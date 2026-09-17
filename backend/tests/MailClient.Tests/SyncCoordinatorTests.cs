using System.Collections.Concurrent;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class SyncCoordinatorTests
{
    [Theory]
    [InlineData(MailConnectionFailure.Network, SyncFailureCategory.Transient)]
    [InlineData(MailConnectionFailure.Authentication, SyncFailureCategory.Authentication)]
    [InlineData(MailConnectionFailure.Tls, SyncFailureCategory.Configuration)]
    public void MapFailure_ReusesConnectionClassification(MailConnectionFailure failure, SyncFailureCategory expected)
    {
        Assert.Equal(expected, SyncCoordinator.MapFailure(new MailConnectionException(failure, "x")));
    }

    [Fact]
    public void MapFailure_UnknownProtocolFailureIsNotRetried()
    {
        var failure = SyncCoordinator.MapFailure(new MailConnectionException(MailConnectionFailure.Protocol, "UID FETCH failed: bogus"));
        Assert.Equal(SyncFailureCategory.Permanent, failure);
    }

    [Fact]
    public void Classifier_NetworkIsTransient_AuthenticationIsNot()
    {
        Assert.Equal(SyncFailureCategory.Transient, SyncFailureClassifier.Classify(new TimeoutException()));
        Assert.Equal(SyncFailureCategory.Authentication, SyncFailureClassifier.Classify(new InvalidOperationException("mail_account_needs_reauthentication")));
        Assert.Equal(SyncFailureCategory.Configuration, SyncFailureClassifier.Classify(new InvalidOperationException("oauth_provider_not_configured")));
        Assert.Equal(SyncFailureCategory.Permanent, SyncFailureClassifier.Classify(new InvalidOperationException("boom")));
    }

    [Fact]
    public void RetryPolicy_UsesIncreasingBoundedDelay_StopsAtMaxAttempts()
    {
        var clock = new FakeSyncClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var retry = SyncRetryPolicy.FromSettings(3, 2, 60, clock, () => 0);

        Assert.True(retry.ShouldRetry(1, SyncFailureCategory.Transient, CancellationToken.None));
        Assert.False(retry.ShouldRetry(1, SyncFailureCategory.Authentication, CancellationToken.None));
        Assert.False(retry.ShouldRetry(3, SyncFailureCategory.Transient, CancellationToken.None));
        Assert.False(retry.ShouldRetry(1, SyncFailureCategory.Transient, new CancellationToken(true)));

        Assert.Equal(TimeSpan.FromSeconds(2), retry.DelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(4), retry.DelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(60), retry.DelayForAttempt(10));
    }

    [Fact]
    public async Task RetryPolicy_WaitsWithoutRealSleep()
    {
        var clock = new FakeSyncClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var retry = SyncRetryPolicy.FromSettings(3, 2, 60, clock, () => 0);

        await retry.WaitForRetryAsync(1, CancellationToken.None);

        Assert.Single(clock.Delays);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Delays[0]);
    }

    [Fact]
    public async Task Coordinator_SameAccountNeverRunsConcurrently_AccountsRunInParallel()
    {
        var entered = new ConcurrentDictionary<Guid, int>();
        var maxConcurrent = new ConcurrentDictionary<Guid, int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new BlockingExecutor(entered, maxConcurrent, gate);
        var harness = CreateHarness(executor, maxAccounts: 2, maxFolders: 1);
        var accountA = await harness.SeedAccountAsync(2);
        var accountB = await harness.SeedAccountAsync(1);

        var foldersA = await harness.FolderIdsAsync(accountA);
        foreach (var folder in foldersA)
            await harness.Scheduler.ScheduleFolderAsync(accountA, folder, SyncOrigin.Periodic, CancellationToken.None);
        var foldersB = await harness.FolderIdsAsync(accountB);
        foreach (var folder in foldersB)
            await harness.Scheduler.ScheduleFolderAsync(accountB, folder, SyncOrigin.Periodic, CancellationToken.None);

        var dispatch = harness.Scheduler.DispatchOnceAsync(CancellationToken.None);
        await Task.Delay(200);
        gate.TrySetResult();
        await dispatch;

        Assert.Equal(1, maxConcurrent[accountA]);
        Assert.Equal(1, maxConcurrent[accountB]);
        Assert.Equal(3, executor.FolderCalls.Count);
    }

    [Fact]
    public async Task Coordinator_InboxRunsBeforeOtherFolders_SuccessResetsFailureState()
    {
        var order = new ConcurrentQueue<Guid>();
        var executor = new OrderRecordingExecutor(order);
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var inboxId = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);
        var otherId = await harness.AddFolderAsync(accountId, MailFolderType.Custom);
        await harness.SetFailureStateAsync(otherId, failures: 2);

        await harness.Scheduler.ScheduleFolderAsync(accountId, otherId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.ScheduleFolderAsync(accountId, inboxId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal([inboxId, otherId], order.ToArray());
        Assert.Equal(0, await harness.ConsecutiveFailuresAsync(otherId));
        Assert.NotNull(await harness.LastSuccessfulSyncAtAsync(otherId));
    }

    [Fact]
    public async Task Coordinator_ThresholdFailureEmitsSingleSyncError_RecoveryResetsEpisode()
    {
        var executor = new FailingExecutor(new MailConnectionException(MailConnectionFailure.Network, "down"));
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1, threshold: 2, maxAttempts: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Custom);

        await harness.RunPeriodicAsync(accountId, folderId);
        Assert.DoesNotContain(harness.Push.Notifications, n => n.Type == PushEventType.SyncError);

        await harness.RunPeriodicAsync(accountId, folderId);
        Assert.Single(harness.Push.Notifications, n => n.Type == PushEventType.SyncError);

        await harness.RunPeriodicAsync(accountId, folderId);
        Assert.Single(harness.Push.Notifications, n => n.Type == PushEventType.SyncError);

        executor.Failure = null;
        await harness.RunPeriodicAsync(accountId, folderId);

        executor.Failure = new MailConnectionException(MailConnectionFailure.Network, "down again");
        await harness.RunPeriodicAsync(accountId, folderId);
        await harness.RunPeriodicAsync(accountId, folderId);
        Assert.Equal(2, harness.Push.Notifications.Count(n => n.Type == PushEventType.SyncError));
    }

    [Fact]
    public async Task Coordinator_AuthenticationFailureUsesReauthenticationEvent_NotSyncError()
    {
        var executor = new FailingExecutor(new MailConnectionException(MailConnectionFailure.Authentication, "nope"));
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1, threshold: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Custom);

        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(harness.Push.Notifications, n => n.Type == PushEventType.SyncError);
    }

    [Fact]
    public async Task Coordinator_DeletedOrDisabledAccountStopsSafely()
    {
        var executor = new OrderRecordingExecutor(new ConcurrentQueue<Guid>());
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Custom);
        await harness.DisableAccountAsync(accountId);

        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.Empty(executor.FolderCalls);
    }

    [Fact]
    public async Task Coordinator_ConcurrentFolderExecutionsUseSeparateScopes()
    {
        var scopes = new ConcurrentBag<Guid>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString("N");
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton(new SyncScheduleQueue(100));
        services.AddSingleton<ISyncClock>(new FakeSyncClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton<ISyncLockProvider, InMemorySyncLockProvider>();
        services.AddScoped<ISyncExecutor>(_ => new ScopeCapturingExecutor(scopes, gate));
        services.AddSingleton<IRuntimeSettingsStore>(new FakeRuntimeSettingsStore(1, 2, 3));
        services.AddScoped<RuntimeOperationSettings>();
        var push = new FakePushNotificationService();
        services.AddSingleton<IPushNotificationService>(push);
        services.AddSingleton<IRuntimePolicyProvider, DefaultRuntimePolicyProvider>();
        services.AddSingleton<ILogger<SyncCoordinator>>(NullLogger<SyncCoordinator>.Instance);
        services.AddSingleton<SyncCoordinator>();
        var provider = services.BuildServiceProvider();
        var scheduler = provider.GetRequiredService<SyncCoordinator>();

        Guid accountId;
        Guid first;
        Guid second;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            accountId = Guid.NewGuid();
            db.MailAccounts.Add(new MailAccount
            {
                Id = accountId,
                EmailAddress = "scope@example.test",
                NormalizedEmailAddress = "SCOPE@EXAMPLE.TEST",
                Status = MailAccountStatus.Active
            });
            first = Guid.NewGuid();
            second = Guid.NewGuid();
            foreach (var folderId in new[] { first, second })
            {
                db.MailFolders.Add(new MailFolder
                {
                    Id = folderId,
                    MailAccountId = accountId,
                    Name = folderId.ToString("N"),
                    FullName = folderId.ToString("N"),
                    FolderType = MailFolderType.Custom,
                    IsSyncEnabled = true,
                    IsAvailable = true
                });
                db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId });
            }
            await db.SaveChangesAsync();
        }

        await scheduler.ScheduleFolderAsync(accountId, first, SyncOrigin.Periodic, CancellationToken.None);
        await scheduler.ScheduleFolderAsync(accountId, second, SyncOrigin.Periodic, CancellationToken.None);
        var dispatch = scheduler.DispatchOnceAsync(CancellationToken.None);
        await Task.Delay(200);
        gate.TrySetResult();
        await dispatch;

        Assert.Equal(2, scopes.Distinct().Count());
    }

    private static Harness CreateHarness(ISyncExecutor executor, int maxAccounts, int maxFolders, int threshold = 3, int maxAttempts = 3)
    {
        var dbName = Guid.NewGuid().ToString("N");
        var clock = new FakeSyncClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton(new SyncScheduleQueue(100));
        services.AddSingleton<ISyncClock>(clock);
        services.AddSingleton<ISyncLockProvider, InMemorySyncLockProvider>();
        services.AddSingleton(executor);
        services.AddSingleton<IRuntimeSettingsStore>(new FakeRuntimeSettingsStore(maxAccounts, maxFolders, threshold, maxAttempts));
        services.AddScoped<RuntimeOperationSettings>();
        var push = new FakePushNotificationService();
        services.AddSingleton<IPushNotificationService>(push);
        services.AddSingleton<IRuntimePolicyProvider, DefaultRuntimePolicyProvider>();
        services.AddSingleton<ILogger<SyncCoordinator>>(NullLogger<SyncCoordinator>.Instance);
        services.AddSingleton<SyncCoordinator>();
        var provider = services.BuildServiceProvider();
        return new Harness(provider, push, clock);
    }

    private sealed class Harness(ServiceProvider provider, FakePushNotificationService push, FakeSyncClock clock)
    {
        public SyncCoordinator Scheduler => provider.GetRequiredService<SyncCoordinator>();
        public FakePushNotificationService Push => push;

        public async Task RunPeriodicAsync(Guid accountId, Guid folderId)
        {
            clock.Current += TimeSpan.FromMinutes(5);
            Scheduler.DrainPending();
            await Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.Periodic, CancellationToken.None);
            await Scheduler.DispatchOnceAsync(CancellationToken.None);
            await Task.Delay(50);
            Scheduler.DrainPending();
        }

        public async Task<Guid> SeedAccountAsync(int extraFolders)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accountId = Guid.NewGuid();
            db.MailAccounts.Add(new MailAccount
            {
                Id = accountId,
                EmailAddress = $"{accountId:N}@example.test",
                NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
                Status = MailAccountStatus.Active
            });
            for (var i = 0; i < extraFolders; i++)
            {
                var folderId = Guid.NewGuid();
                db.MailFolders.Add(new MailFolder
                {
                    Id = folderId,
                    MailAccountId = accountId,
                    Name = $"Folder {i}",
                    FullName = $"Folder-{i}",
                    FolderType = MailFolderType.Custom,
                    IsSyncEnabled = true,
                    IsAvailable = true
                });
                db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId });
            }
            await db.SaveChangesAsync();
            return accountId;
        }

        public async Task<Guid> AddFolderAsync(Guid accountId, MailFolderType type)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var folderId = Guid.NewGuid();
            db.MailFolders.Add(new MailFolder
            {
                Id = folderId,
                MailAccountId = accountId,
                Name = type.ToString(),
                FullName = $"{type}-{folderId:N}",
                FolderType = type,
                IsSyncEnabled = true,
                IsAvailable = true
            });
            db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId });
            await db.SaveChangesAsync();
            return folderId;
        }

        public async Task<List<Guid>> FolderIdsAsync(Guid accountId)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.MailFolders.Where(f => f.MailAccountId == accountId).Select(f => f.Id).ToListAsync();
        }

        public async Task SetFailureStateAsync(Guid folderId, int failures)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.SyncStates.SingleAsync(s => s.MailFolderId == folderId);
            state.ConsecutiveFailures = failures;
            await db.SaveChangesAsync();
        }

        public async Task<int> ConsecutiveFailuresAsync(Guid folderId)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.SyncStates.Where(s => s.MailFolderId == folderId).Select(s => s.ConsecutiveFailures).SingleAsync();
        }

        public async Task<DateTime?> LastSuccessfulSyncAtAsync(Guid folderId)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.SyncStates.Where(s => s.MailFolderId == folderId).Select(s => s.LastSuccessfulSyncAt).SingleAsync();
        }

        public async Task DisableAccountAsync(Guid accountId)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var account = await db.MailAccounts.SingleAsync(a => a.Id == accountId);
            account.Status = MailAccountStatus.Disabled;
            await db.SaveChangesAsync();
        }
    }

    private sealed class FakeRuntimeSettingsStore(int maxAccounts, int maxFolders, int threshold, int maxAttempts = 3) : IRuntimeSettingsStore
    {
        public Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeSettingsSnapshot(new RuntimeSettings
            {
                Sync = new RuntimeSyncSettings
                {
                    MaxConcurrentAccounts = maxAccounts,
                    MaxConcurrentFoldersPerAccount = maxFolders,
                    SyncErrorFailureThreshold = threshold,
                    TransientRetryMaxAttempts = maxAttempts
                }
            }, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        public Task<RuntimeSettingsSnapshot> ReplaceAsync(int expectedVersion, RuntimeSettings settings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingExecutor(
        ConcurrentDictionary<Guid, int> entered,
        ConcurrentDictionary<Guid, int> maxConcurrent,
        TaskCompletionSource gate) : ISyncExecutor
    {
        public ConcurrentBag<(Guid AccountId, Guid FolderId)> FolderCalls { get; } = [];
        public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            var current = entered.AddOrUpdate(accountId, 1, (_, count) => count + 1);
            maxConcurrent.AddOrUpdate(accountId, current, (_, max) => Math.Max(max, current));
            try
            {
                FolderCalls.Add((accountId, folderId));
                await gate.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                entered.AddOrUpdate(accountId, 0, (_, count) => count - 1);
            }
        }
    }

    private sealed class OrderRecordingExecutor(ConcurrentQueue<Guid> order) : ISyncExecutor
    {
        public ConcurrentBag<(Guid AccountId, Guid FolderId)> FolderCalls { get; } = [];
        public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            FolderCalls.Add((accountId, folderId));
            order.Enqueue(folderId);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingExecutor(Exception? failure) : ISyncExecutor
    {
        public Exception? Failure { get; set; } = failure;
        public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) =>
            Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    private sealed class ScopeCapturingExecutor(ConcurrentBag<Guid> scopes, TaskCompletionSource gate) : ISyncExecutor
    {
        private readonly Guid _scopeId = Guid.NewGuid();
        public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            scopes.Add(_scopeId);
            await gate.Task.WaitAsync(cancellationToken);
        }
    }
}
