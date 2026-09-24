using System.Collections.Concurrent;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
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
    public async Task Coordinator_SlowAccount_DoesNotDelayOtherAccounts()
    {
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new PerAccountGateExecutor(slowGate);
        var harness = CreateHarness(executor, maxAccounts: 2, maxFolders: 1);
        var slow = await harness.SeedAccountAsync(1);
        var fast = await harness.SeedAccountAsync(1);
        executor.SlowAccountId = slow;
        await harness.Scheduler.StartAsync(CancellationToken.None);
        try
        {
            await harness.Scheduler.ScheduleFolderAsync(slow, (await harness.FolderIdsAsync(slow))[0], SyncOrigin.UserRequested, CancellationToken.None);
            await WaitUntilAsync(() => executor.Started.Contains(slow));

            await harness.Scheduler.ScheduleFolderAsync(fast, (await harness.FolderIdsAsync(fast))[0], SyncOrigin.UserRequested, CancellationToken.None);

            await WaitUntilAsync(() => executor.Completed.Contains(fast));
            Assert.DoesNotContain(slow, executor.Completed);
        }
        finally
        {
            slowGate.TrySetResult();
            await harness.Scheduler.StopAsync(CancellationToken.None);
        }
    }

    private sealed class PerAccountGateExecutor(TaskCompletionSource slowGate) : ISyncExecutor
    {
        public Guid SlowAccountId { get; set; }
        public ConcurrentBag<Guid> Started { get; } = [];
        public ConcurrentBag<Guid> Completed { get; } = [];
        public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            Started.Add(accountId);
            if (accountId == SlowAccountId)
                await slowGate.Task;
            Completed.Add(accountId);
        }
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
        services.AddSingleton<ISyncConnectionBudget, SyncConnectionBudget>();
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

    [Fact]
    public async Task Metrics_SuccessAfterFailures_RecordsCompletionRecoveryAndBalancedActivity()
    {
        using var capture = new MetricsCapture();
        var harness = CreateHarness(new OrderRecordingExecutor(new ConcurrentQueue<Guid>()), maxAccounts: 1, maxFolders: 1, metrics: capture.Metrics);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);
        await harness.SetFailureStateAsync(folderId, failures: 2);

        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(1, capture.Sum("mailclient.sync.scheduled", ("origin", "periodic")));
        Assert.Equal(1, capture.Sum("mailclient.sync.completed", ("origin", "periodic"), ("folder_type", "inbox")));
        Assert.Equal(1, capture.Sum("mailclient.sync.recoveries", ("folder_type", "inbox")));
        Assert.Single(capture.For("mailclient.sync.duration"), item => item.Has("result", "success"));
        Assert.Empty(capture.For("mailclient.sync.failures"));
        Assert.Equal(1, capture.Sum("mailclient.lock.acquisitions", ("purpose", "account_sync"), ("result", "acquired")));
        Assert.Equal(0, capture.Sum("mailclient.sync.active.accounts"));
        Assert.Equal(0, capture.Sum("mailclient.sync.active.folders"));
        Assert.Single(capture.For("mailclient.sync.connection_budget.wait"));
    }

    [Theory]
    [InlineData(MailConnectionFailure.Tls, "configuration")]
    [InlineData(MailConnectionFailure.Authentication, "authentication")]
    [InlineData(MailConnectionFailure.Network, "transient")]
    public async Task Metrics_Failure_RecordsClassifiedCategory(MailConnectionFailure failure, string category)
    {
        using var capture = new MetricsCapture();
        var harness = CreateHarness(new FailingExecutor(new MailConnectionException(failure, "x")), maxAccounts: 1, maxFolders: 1, metrics: capture.Metrics);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Custom);

        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.UserRequested, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(1, capture.Sum("mailclient.sync.failures", ("origin", "user"), ("failure_category", category)));
        Assert.Empty(capture.For("mailclient.sync.completed"));
    }

    [Fact]
    public async Task Metrics_TransientFailure_RecordsRetryThenExhaustion()
    {
        using var retrying = new MetricsCapture();
        var harness = CreateHarness(new FailingExecutor(new TimeoutException()), maxAccounts: 1, maxFolders: 1, maxAttempts: 3, metrics: retrying.Metrics);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Custom);
        await harness.RunPeriodicAsync(accountId, folderId);

        Assert.Equal(1, retrying.Sum("mailclient.sync.retries", ("origin", "periodic"), ("failure_category", "transient")));
        Assert.Empty(retrying.For("mailclient.sync.retries.exhausted"));

        using var exhausted = new MetricsCapture();
        var single = CreateHarness(new FailingExecutor(new TimeoutException()), maxAccounts: 1, maxFolders: 1, maxAttempts: 1, metrics: exhausted.Metrics);
        var otherAccountId = await single.SeedAccountAsync(0);
        var otherFolderId = await single.AddFolderAsync(otherAccountId, MailFolderType.Custom);
        await single.RunPeriodicAsync(otherAccountId, otherFolderId);
        await single.RunPeriodicAsync(otherAccountId, otherFolderId);

        Assert.Empty(exhausted.For("mailclient.sync.retries"));
        Assert.Equal(1, exhausted.Sum("mailclient.sync.retries.exhausted", ("origin", "periodic")));
    }

    [Fact]
    public async Task Metrics_QueueFull_RecordsRejectionAndShedding()
    {
        using var capture = new MetricsCapture();
        var harness = CreateHarness(new OrderRecordingExecutor(new ConcurrentQueue<Guid>()), maxAccounts: 1, maxFolders: 1, metrics: capture.Metrics, queueCapacity: 1);
        var accountId = Guid.NewGuid();

        await harness.Scheduler.ScheduleFolderAsync(accountId, Guid.NewGuid(), SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.ScheduleFolderAsync(accountId, Guid.NewGuid(), SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.ScheduleFolderAsync(accountId, Guid.NewGuid(), SyncOrigin.UserRequested, CancellationToken.None);

        Assert.Equal(1, capture.Sum("mailclient.sync.queue.rejected", ("origin", "periodic"), ("reason", "queue_full")));
        Assert.Equal(1, capture.Sum("mailclient.sync.queue.rejected", ("origin", "periodic"), ("reason", "shed")));
        Assert.Equal(1, capture.Sum("mailclient.sync.scheduled", ("origin", "periodic")));
        Assert.Equal(1, capture.Sum("mailclient.sync.scheduled", ("origin", "user")));
    }

    [Fact]
    public async Task Coordinator_ReconciliationReachesFolderThatIsNotPeriodicallySynced()
    {
        var executor = new OrderRecordingExecutor(new ConcurrentQueue<Guid>());
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var trashId = await harness.AddFolderAsync(accountId, MailFolderType.Trash, isSyncEnabled: false);

        await harness.Scheduler.ScheduleFolderAsync(accountId, trashId, SyncOrigin.Reconciliation, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal([trashId], executor.FolderCalls.Select(call => call.FolderId));
    }

    [Fact]
    public async Task Coordinator_PeriodicWorkStillSkipsFoldersThatAreNotSyncEnabled()
    {
        var executor = new OrderRecordingExecutor(new ConcurrentQueue<Guid>());
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var trashId = await harness.AddFolderAsync(accountId, MailFolderType.Trash, isSyncEnabled: false);

        await harness.Scheduler.ScheduleFolderAsync(accountId, trashId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.Empty(executor.FolderCalls);
    }

    [Fact]
    public async Task Coordinator_LockContention_RequeuesImmediately()
    {
        var executor = new OrderRecordingExecutor(new ConcurrentQueue<Guid>());
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1, locks: new StubSyncLockProvider(SyncLockStatus.Contended));
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);

        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.Empty(executor.FolderCalls);
        Assert.Equal(1, harness.Scheduler.PendingCount);
        Assert.Empty(harness.Clock.Delays);
    }

    [Fact]
    public async Task Coordinator_LockInfrastructureFailure_DefersRequeueInsteadOfTightLoop()
    {
        var executor = new OrderRecordingExecutor(new ConcurrentQueue<Guid>());
        var locks = new StubSyncLockProvider(SyncLockStatus.InfrastructureFailure);
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1, locks: locks);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);

        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Scheduler.PendingCount == 1);

        Assert.Empty(executor.FolderCalls);
        Assert.Equal(1, locks.Calls);
        Assert.Equal(new[] { SyncCoordinator.LockFailureRequeueDelay }, harness.Clock.Delays);
    }

    [Fact]
    public async Task UserSyncJobs_CompleteOnlyAfterSharedFolderExecutionFinishes()
    {
        var entered = new ConcurrentDictionary<Guid, int>();
        var maxConcurrent = new ConcurrentDictionary<Guid, int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new BlockingExecutor(entered, maxConcurrent, gate);
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);
        var first = await harness.Scheduler.ScheduleUserFolderJobAsync(accountId, folderId, CancellationToken.None);
        var second = await harness.Scheduler.ScheduleUserFolderJobAsync(accountId, folderId, CancellationToken.None);
        Assert.NotEqual(first, second);
        Assert.Equal(1, harness.Scheduler.PendingCount);
        Assert.Equal("queued", harness.Scheduler.GetJobStatus(accountId, first)?.Status);

        var dispatch = harness.Scheduler.DispatchOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => entered.ContainsKey(accountId));
        Assert.Equal("running", harness.Scheduler.GetJobStatus(accountId, first)?.Status);
        Assert.Equal("running", harness.Scheduler.GetJobStatus(accountId, second)?.Status);
        Assert.Null(harness.Scheduler.GetJobStatus(Guid.NewGuid(), first));
        gate.SetResult();
        await dispatch;

        Assert.Equal("succeeded", harness.Scheduler.GetJobStatus(accountId, first)?.Status);
        Assert.Equal("succeeded", harness.Scheduler.GetJobStatus(accountId, second)?.Status);
        Assert.Null(harness.Scheduler.GetJobStatus(accountId, first)?.ErrorCode);
        Assert.Single(executor.FolderCalls);
    }

    [Fact]
    public async Task UserSyncJob_ReportsExecutorFailureRatherThanSuccessfulEnqueue()
    {
        var executor = new FailingExecutor(new MailConnectionException(MailConnectionFailure.Authentication, "bad secret"));
        var harness = CreateHarness(executor, maxAccounts: 1, maxFolders: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);
        var jobId = await harness.Scheduler.ScheduleUserFolderJobAsync(accountId, folderId, CancellationToken.None);

        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        var status = harness.Scheduler.GetJobStatus(accountId, jobId);
        Assert.Equal("failed", status?.Status);
        Assert.Equal("mail_authentication_failed", status?.ErrorCode);
    }

    [Fact]
    public async Task UserSyncJob_QueueFullAndAbandonedWorkNeverReportSuccess()
    {
        var harness = CreateHarness(new OrderRecordingExecutor(new ConcurrentQueue<Guid>()), maxAccounts: 1, maxFolders: 1, queueCapacity: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var firstFolder = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);
        var secondFolder = await harness.AddFolderAsync(accountId, MailFolderType.Custom);
        var jobId = await harness.Scheduler.ScheduleUserFolderJobAsync(accountId, firstFolder, CancellationToken.None);

        await Assert.ThrowsAsync<SyncQueueFullException>(async () =>
            await harness.Scheduler.ScheduleUserFolderJobAsync(accountId, secondFolder, CancellationToken.None));
        Assert.Equal("queued", harness.Scheduler.GetJobStatus(accountId, jobId)?.Status);
        harness.Scheduler.DrainPending();

        var abandoned = harness.Scheduler.GetJobStatus(accountId, jobId);
        Assert.Equal("failed", abandoned?.Status);
        Assert.Equal("sync_interrupted", abandoned?.ErrorCode);
        harness.Clock.Current += TimeSpan.FromHours(2);
        Assert.Null(harness.Scheduler.GetJobStatus(accountId, jobId));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    internal static Harness CreateHarness(
        ISyncExecutor executor,
        int maxAccounts,
        int maxFolders,
        int threshold = 3,
        int maxAttempts = 3,
        MailClientMetrics? metrics = null,
        int queueCapacity = 100,
        ISyncLockProvider? locks = null)
    {
        var dbName = Guid.NewGuid().ToString("N");
        var clock = new FakeSyncClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton(new SyncScheduleQueue(queueCapacity));
        services.AddSingleton<ISyncClock>(clock);
        if (metrics is not null)
            services.AddSingleton(metrics);
        if (locks is null)
            services.AddSingleton<ISyncLockProvider, InMemorySyncLockProvider>();
        else
            services.AddSingleton(locks);
        services.AddSingleton<ISyncConnectionBudget, SyncConnectionBudget>();
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

    internal sealed class Harness(ServiceProvider provider, FakePushNotificationService push, FakeSyncClock clock)
    {
        public SyncCoordinator Scheduler => provider.GetRequiredService<SyncCoordinator>();
        public FakePushNotificationService Push => push;
        public FakeSyncClock Clock => clock;

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

        public async Task<Guid> AddFolderAsync(Guid accountId, MailFolderType type, bool isSyncEnabled = true)
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
                IsSyncEnabled = isSyncEnabled,
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

    internal sealed class OrderRecordingExecutor(ConcurrentQueue<Guid> order) : ISyncExecutor
    {
        public ConcurrentBag<(Guid AccountId, Guid FolderId)> FolderCalls { get; } = [];
        public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            FolderCalls.Add((accountId, folderId));
            order.Enqueue(folderId);
            return Task.CompletedTask;
        }
    }

    internal sealed class FailingExecutor(Exception? failure) : ISyncExecutor
    {
        public Exception? Failure { get; set; } = failure;
        public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) =>
            Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    private sealed class ScopeCapturingExecutor(ConcurrentBag<Guid> scopes, TaskCompletionSource gate) : ISyncExecutor
    {
        private readonly Guid _scopeId = Guid.NewGuid();
        public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            scopes.Add(_scopeId);
            await gate.Task.WaitAsync(cancellationToken);
        }
    }
}
