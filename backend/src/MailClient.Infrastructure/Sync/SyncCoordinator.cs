using System.Diagnostics;
using System.Threading.Channels;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Application.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Sync;

public sealed class SyncCoordinator : BackgroundService, ISyncScheduler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SyncScheduleQueue _queue;
    private readonly ISyncClock _clock;
    private readonly ISyncConnectionBudget _connectionBudget;
    private readonly Func<double> _jitterSource;
    private readonly ILogger<SyncCoordinator> _logger;
    private readonly MailClientMetrics? _metrics;
    private readonly Channel<SyncScheduleSignal> _signals = Channel.CreateUnbounded<SyncScheduleSignal>();
    private readonly object _runningGate = new();
    private readonly Dictionary<Guid, Task> _runningAccounts = [];
    private readonly object _jobGate = new();
    private readonly SyncJobRegistry _jobs;

    public SyncCoordinator(
        IServiceScopeFactory scopes,
        SyncScheduleQueue queue,
        ISyncClock clock,
        ISyncConnectionBudget connectionBudget,
        ILogger<SyncCoordinator> logger,
        Func<double>? jitterSource = null,
        MailClientMetrics? metrics = null)
    {
        _scopes = scopes;
        _queue = queue;
        _clock = clock;
        _connectionBudget = connectionBudget;
        _logger = logger;
        _jitterSource = jitterSource ?? Random.Shared.NextDouble;
        _metrics = metrics;
        _jobs = new SyncJobRegistry(clock);
    }

    internal static readonly TimeSpan LockFailureRequeueDelay = TimeSpan.FromSeconds(30);

    public ValueTask ScheduleAsync(ScheduledSyncRequest request, CancellationToken cancellationToken)
    {
        Enqueue(request, newWork: true);
        return _signals.Writer.WriteAsync(new SyncScheduleSignal(), cancellationToken);
    }

    private void Enqueue(ScheduledSyncRequest request, bool newWork = false)
    {
        SyncEnqueueResult result;
        try
        {
            lock (_jobGate)
                result = _queue.Enqueue(request);
        }
        catch (SyncQueueFullException)
        {
            _metrics?.RecordSyncQueueRejected(request.Origin, "queue_full");
            throw;
        }

        switch (result)
        {
            case SyncEnqueueResult.Rejected:
                _metrics?.RecordSyncQueueRejected(request.Origin, "queue_full");
                break;
            case SyncEnqueueResult.EnqueuedAfterShedding:
                _metrics?.RecordSyncQueueRejected(SyncOrigin.Periodic, "shed");
                if (newWork)
                    _metrics?.RecordSyncScheduled(request.Origin);
                break;
            case SyncEnqueueResult.Enqueued when newWork:
                _metrics?.RecordSyncScheduled(request.Origin);
                break;
        }
    }

    public ValueTask<Guid> ScheduleUserFolderJobAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = SyncScheduling.ForUserFolder(accountId, folderId, _queue.NextSequence());
        Guid jobId;
        lock (_jobGate)
        {
            _jobs.EnsureCapacity();
            Enqueue(request, newWork: true);
            jobId = _jobs.Create(request.Request);
        }
        _signals.Writer.TryWrite(new SyncScheduleSignal());
        return ValueTask.FromResult(jobId);
    }

    public SyncJobStatus? GetJobStatus(Guid accountId, Guid jobId)
    {
        lock (_jobGate)
            return _jobs.Get(accountId, jobId);
    }

    public async ValueTask ScheduleAccountAsync(Guid accountId, SyncOrigin origin, CancellationToken cancellationToken)
    {
        var request = origin switch
        {
            SyncOrigin.UserRequested => SyncScheduling.ForUserAccount(accountId, _queue.NextSequence()),
            _ => SyncScheduling.ForInitialAccount(accountId, _queue.NextSequence())
        };
        await ScheduleAsync(request, cancellationToken);
    }

    public async ValueTask ScheduleFolderAsync(Guid accountId, Guid folderId, SyncOrigin origin, CancellationToken cancellationToken)
    {
        ScheduledSyncRequest request = origin switch
        {
            SyncOrigin.UserRequested => SyncScheduling.ForUserFolder(accountId, folderId, _queue.NextSequence()),
            SyncOrigin.Reconciliation => SyncScheduling.ForReconciliationFolder(accountId, folderId, _queue.NextSequence()),
            _ => await ForPeriodicFolderAsync(accountId, folderId, cancellationToken)
        };
        await ScheduleAsync(request, cancellationToken);
    }

    private async Task<ScheduledSyncRequest> ForPeriodicFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var folderType = await db.MailFolders.AsNoTracking()
            .Where(folder => folder.Id == folderId && folder.MailAccountId == accountId)
            .Select(folder => (MailFolderType?)folder.FolderType)
            .SingleOrDefaultAsync(cancellationToken);
        return SyncScheduling.ForPeriodicFolder(accountId, folderId, folderType ?? MailFolderType.Unknown, _queue.NextSequence());
    }

    public void NotifySettingsChanged()
    {
        Interlocked.Exchange(ref _settingsExpiresAt, 0);
        _signals.Writer.TryWrite(new SyncScheduleSignal());
    }

    public int PendingCount => _queue.Count;

    public IReadOnlyList<ScheduledSyncRequest> DrainPending()
    {
        lock (_jobGate)
        {
            var drained = _queue.Drain();
            foreach (var item in drained)
                _jobs.FailQueued(item.Request, "sync_interrupted");
            return drained;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverPendingReconciliationAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                RuntimeSettings settings = await CurrentSettingsAsync(stoppingToken);
                if (!settings.Sync.Enabled)
                {
                    await WaitForSignalAsync(TimeSpan.FromSeconds(settings.Sync.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                StartReadyWork(settings, stoppingToken);
                await WaitForSignalAsync(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
        finally
        {
            Task[] running;
            lock (_runningGate)
                running = [.. _runningAccounts.Values];
            await Task.WhenAll(running).ContinueWith(static _ => { }, TaskScheduler.Default);
            lock (_jobGate)
                _jobs.FailAll("sync_interrupted");
        }
    }

    internal async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await CurrentSettingsAsync(cancellationToken);
        if (!settings.Sync.Enabled)
            return;
        await Task.WhenAll(StartReadyWork(settings, cancellationToken));
    }

    // The loop ticks every 250 ms; re-reading settings from the DB each tick is wasteful. Edits apply within
    // SettingsCacheTtl, or immediately through NotifySettingsChanged.
    private static readonly TimeSpan SettingsCacheTtl = TimeSpan.FromSeconds(5);
    private RuntimeSettings? _settings;
    private long _settingsExpiresAt;

    private async Task<RuntimeSettings> CurrentSettingsAsync(CancellationToken cancellationToken)
    {
        if (_settings is not null && Environment.TickCount64 < Interlocked.Read(ref _settingsExpiresAt))
            return _settings;

        await using var scope = _scopes.CreateAsyncScope();
        var operationSettings = scope.ServiceProvider.GetRequiredService<RuntimeOperationSettings>();
        var settings = (await operationSettings.GetAsync(cancellationToken)).Settings;
        _queue.UpdateCapacity(settings.Sync.QueueCapacity);
        _settings = settings;
        Interlocked.Exchange(ref _settingsExpiresAt, Environment.TickCount64 + (long)SettingsCacheTtl.TotalMilliseconds);
        return settings;
    }

    /// <summary>
    /// Starts queued work for accounts that are not already syncing, up to MaxConcurrentAccounts in flight, without
    /// waiting for it: a slow account must not delay other accounts' (especially user-requested) syncs. Work for a
    /// busy account stays queued until that account's run finishes.
    /// </summary>
    private List<Task> StartReadyWork(RuntimeSettings settings, CancellationToken cancellationToken)
    {
        lock (_jobGate)
        {
            var groups = new Dictionary<Guid, List<(ScheduledSyncRequest Item, IReadOnlyList<Guid> Jobs)>>();
            var deferred = new List<ScheduledSyncRequest>();
            int running;
            lock (_runningGate)
                running = _runningAccounts.Count;
            while (_queue.TryDequeue(out var next))
            {
                var accountId = next.Request.AccountId;
                bool busy;
                lock (_runningGate)
                    busy = _runningAccounts.ContainsKey(accountId);
                if (busy || (!groups.ContainsKey(accountId) && running + groups.Count >= settings.Sync.MaxConcurrentAccounts))
                {
                    deferred.Add(next);
                    continue;
                }

                if (!groups.TryGetValue(accountId, out var group))
                {
                    group = [];
                    groups[accountId] = group;
                }
                group.Add((next, _jobs.Claim(next.Request)));
            }

            foreach (var item in deferred)
            {
                try
                {
                    Enqueue(item);
                }
                catch (SyncQueueFullException)
                {
                    _jobs.FailQueued(item.Request, "sync_queue_full");
                }
            }

            var started = new List<Task>(groups.Count);
            foreach (var (accountId, group) in groups)
            {
                var task = RunAccountGroupAsync(group, settings, cancellationToken);
                lock (_runningGate)
                    _runningAccounts[accountId] = task;
                task.ContinueWith(completed =>
                {
                    lock (_runningGate)
                        _runningAccounts.Remove(accountId);
                    if (completed.IsFaulted)
                        _logger.LogError(completed.Exception, "Account sync run failed unexpectedly.");
                    _signals.Writer.TryWrite(new SyncScheduleSignal());
                }, TaskScheduler.Default);
                started.Add(task);
            }
            return started;
        }
    }

    private async Task RunAccountGroupAsync(List<(ScheduledSyncRequest Item, IReadOnlyList<Guid> Jobs)> group, RuntimeSettings settings, CancellationToken cancellationToken)
    {
        var ordered = group
            .OrderBy(entry => (int)entry.Item.Priority)
            .ThenBy(entry => entry.Item.Sequence)
            .ToList();
        foreach (var (item, jobs) in ordered)
        {
            try
            {
                var errorCode = await RunAccountAsync(item, settings, cancellationToken);
                lock (_jobGate)
                {
                    if (errorCode == "sync_deferred")
                        _jobs.Defer(item.Request, jobs);
                    else
                        _jobs.Complete(jobs, errorCode);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_jobGate)
                {
                    foreach (var pending in ordered)
                        _jobs.Complete(pending.Jobs, "sync_interrupted");
                }
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Account sync run failed unexpectedly.");
                lock (_jobGate)
                    _jobs.Complete(jobs, "sync_failed");
            }
        }
    }

    private async Task<string?> RunAccountAsync(ScheduledSyncRequest item, RuntimeSettings settings, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var locks = provider.GetRequiredService<ISyncLockProvider>();
        await using var accountLock = await locks.TryAcquireAsync(item.Request.AccountId, SyncLockPurpose.AccountSync, cancellationToken);
        if (accountLock.Status == SyncLockStatus.Contended)
        {
            Enqueue(item);
            return "sync_deferred";
        }

        if (accountLock.Status == SyncLockStatus.InfrastructureFailure)
        {
            _logger.LogWarning(
                "Sync lock unavailable; {Origin} sync deferred for {DelaySeconds} seconds.",
                MailClientTelemetry.Origin(item.Origin), LockFailureRequeueDelay.TotalSeconds);
            RequeueAfter(item, LockFailureRequeueDelay, cancellationToken);
            return "sync_deferred";
        }

        using var activity = MailClientTelemetry.StartActivity("mailclient.sync.account");
        activity?.SetTag("sync.origin", MailClientTelemetry.Origin(item.Origin));
        _metrics?.AddActiveSyncAccounts(1);
        try
        {
            var db = provider.GetRequiredService<AppDbContext>();
            var account = await db.MailAccounts.AsNoTracking()
                .SingleOrDefaultAsync(entry => entry.Id == item.Request.AccountId, cancellationToken);
            if (account is null)
                return "mail_account_not_found";
            if (account.Status == MailAccountStatus.Disabled)
                return "mail_account_disabled";
            if (account.Status == MailAccountStatus.NeedsReauthentication)
                return "mail_account_needs_reauthentication";
            activity?.SetTag("mail.provider", MailClientTelemetry.Provider(account.Provider));
            if (!await ProviderAllowsAsync(provider, account, cancellationToken))
                return "mail_provider_unavailable";

            var folders = await SelectFoldersAsync(db, item, cancellationToken);
            if (folders.Count == 0)
                return "mail_folder_unavailable";

            var folderBudget = Math.Max(1, settings.Sync.MaxConcurrentFoldersPerAccount);
            using var folderGate = new SemaphoreSlim(folderBudget, folderBudget);
            var host = account.ImapHost.Trim().ToLowerInvariant();
            var tasks = folders.Select(folder => RunFolderAsync(item, folder, settings, host, folderGate, cancellationToken));
            var results = await Task.WhenAll(tasks);
            return results.FirstOrDefault(error => error is not null);
        }
        finally
        {
            _metrics?.AddActiveSyncAccounts(-1);
        }
    }

    private async Task<string?> RunFolderAsync(
        ScheduledSyncRequest item,
        FolderTarget folder,
        RuntimeSettings settings,
        string host,
        SemaphoreSlim folderGate,
        CancellationToken cancellationToken)
    {
        await folderGate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var db = provider.GetRequiredService<AppDbContext>();
            if (!await db.MailAccounts.AnyAsync(entry => entry.Id == item.Request.AccountId, cancellationToken))
                return "mail_account_not_found";

            var state = await db.SyncStates.SingleOrDefaultAsync(entry => entry.MailFolderId == folder.FolderId, cancellationToken);
            if (state?.NextRetryAt is { } nextRetry && nextRetry > _clock.UtcNow)
                return "sync_retry_deferred";
            var previousFailures = state?.ConsecutiveFailures ?? 0;

            using var activity = MailClientTelemetry.StartActivity("mailclient.sync.folder");
            activity?.SetTag("sync.origin", MailClientTelemetry.Origin(item.Origin));
            activity?.SetTag("mail.folder_type", MailClientTelemetry.FolderType(folder.FolderType));
            _metrics?.AddActiveSyncFolders(1);
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var hostLease = await _connectionBudget.AcquireAsync(
                    host,
                    settings.Sync.MaxConcurrentSyncConnectionsPerHost,
                    cancellationToken);
                _metrics?.RecordSyncConnectionBudgetWait(Stopwatch.GetElapsedTime(started));
                return await ExecuteFolderAsync(provider, db, item, folder, previousFailures, settings, started, activity, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _metrics?.RecordSyncFolderCancelled(item.Origin, folder.FolderType, Stopwatch.GetElapsedTime(started));
                throw;
            }
            finally
            {
                _metrics?.AddActiveSyncFolders(-1);
            }
        }
        finally
        {
            folderGate.Release();
        }
    }

    private async Task<string?> ExecuteFolderAsync(
        IServiceProvider provider,
        AppDbContext db,
        ScheduledSyncRequest item,
        FolderTarget folder,
        int previousFailures,
        RuntimeSettings settings,
        long started,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        try
        {
            var executor = provider.GetRequiredService<ISyncExecutor>();
            await executor.SyncFolderAsync(item.Request.AccountId, folder.FolderId, cancellationToken);
            await RecordSuccessAsync(db, folder.FolderId, cancellationToken);
            activity?.SetTag("sync.result", "success");
            _metrics?.RecordSyncFolderCompleted(item.Origin, folder.FolderType, Stopwatch.GetElapsedTime(started), previousFailures > 0);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var category = MapFailure(ex);
            activity?.SetTag("sync.result", "failure");
            activity?.SetTag("sync.failure_category", MailClientTelemetry.FailureCategory(category));
            MailClientTelemetry.MarkFailed(activity, ex);
            _metrics?.RecordSyncFolderFailed(item.Origin, folder.FolderType, category, Stopwatch.GetElapsedTime(started));

            var handled = await RecordFailureAsync(provider, db, item, folder, ex, settings, cancellationToken);
            if (handled == SyncFailureOutcome.RetryTransient)
            {
                var retry = SyncRetryPolicy.FromSettings(
                    settings.Sync.TransientRetryMaxAttempts,
                    settings.Sync.RetryBaseDelaySeconds,
                    settings.Sync.RetryMaxDelaySeconds,
                    _clock,
                    _jitterSource);
                var attempts = await ConsecutiveFailuresAsync(db, folder.FolderId, cancellationToken);
                if (retry.ShouldRetry(attempts, SyncFailureClassifier.Classify(ex), cancellationToken))
                {
                    _metrics?.RecordSyncRetryScheduled(item.Origin);
                    RequeueAfter(item, retry.DelayForAttempt(attempts), cancellationToken);
                }
                else if (attempts == retry.MaxAttempts)
                {
                    _metrics?.RecordSyncRetriesExhausted(item.Origin);
                }
            }
            return category switch
            {
                SyncFailureCategory.Authentication => "mail_authentication_failed",
                SyncFailureCategory.Configuration => "mail_provider_unavailable",
                SyncFailureCategory.Transient => "mail_provider_unavailable",
                _ => "sync_failed"
            };
        }
    }

    private void RequeueAfter(ScheduledSyncRequest item, TimeSpan delay, CancellationToken cancellationToken)
    {
        var requeue = item with { Sequence = _queue.NextSequence() };
        _ = Task.Run(async () =>
        {
            try
            {
                await _clock.DelayAsync(delay, cancellationToken);
                Enqueue(requeue);
                _signals.Writer.TryWrite(new SyncScheduleSignal());
            }
            catch (OperationCanceledException)
            {
                lock (_jobGate)
                    _jobs.FailQueued(item.Request, "sync_interrupted");
            }
            catch (SyncQueueFullException)
            {
                lock (_jobGate)
                    _jobs.FailQueued(item.Request, "sync_queue_full");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deferred sync could not be requeued.");
                lock (_jobGate)
                    _jobs.FailQueued(item.Request, "sync_failed");
            }
        }, cancellationToken);
    }

    private static async Task<bool> ProviderAllowsAsync(IServiceProvider provider, MailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            var policy = provider.GetRequiredService<IRuntimePolicyProvider>();
            (await policy.GetAsync(cancellationToken)).EnsureExistingAccountAllowed(account.Provider);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<List<FolderTarget>> SelectFoldersAsync(AppDbContext db, ScheduledSyncRequest item, CancellationToken cancellationToken)
    {
        if (item.Request.FolderId is { } folderId)
        {
            var single = await db.MailFolders.AsNoTracking()
                .Where(folder => folder.Id == folderId
                    && folder.MailAccountId == item.Request.AccountId
                    && folder.IsAvailable
                    && (item.Origin == SyncOrigin.UserRequested
                        || item.Origin == SyncOrigin.Reconciliation
                        || folder.IsSyncEnabled))
                .Select(folder => new FolderTarget(folder.Id, folder.FolderType))
                .SingleOrDefaultAsync(cancellationToken);
            return single is null ? [] : [single];
        }

        return await db.MailFolders.AsNoTracking()
            .Where(folder => folder.MailAccountId == item.Request.AccountId && folder.IsSyncEnabled && folder.IsAvailable)
            .OrderBy(folder => folder.FolderType == MailFolderType.Inbox ? 0 : 1)
            .ThenBy(folder => folder.Id)
            .Select(folder => new FolderTarget(folder.Id, folder.FolderType))
            .ToListAsync(cancellationToken);
    }

    private async Task RecordSuccessAsync(AppDbContext db, Guid folderId, CancellationToken cancellationToken)
    {
        var state = await db.SyncStates.SingleOrDefaultAsync(entry => entry.MailFolderId == folderId, cancellationToken);
        if (state is null)
            return;
        state.ConsecutiveFailures = 0;
        state.LastFailureCategory = null;
        state.LastFailureAt = null;
        state.NextRetryAt = null;
        state.LastSuccessfulSyncAt = _clock.UtcNow;
        state.LastErrorNotifiedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SyncFailureOutcome> RecordFailureAsync(
        IServiceProvider provider,
        AppDbContext db,
        ScheduledSyncRequest item,
        FolderTarget folder,
        Exception exception,
        RuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        if (exception is MailConnectionException connection && connection.Failure == MailConnectionFailure.Authentication)
            return SyncFailureOutcome.Authentication;

        var category = MapFailure(exception);
        if (category == SyncFailureCategory.Authentication)
            return SyncFailureOutcome.Authentication;

        var folderId = folder.FolderId;
        var state = await db.SyncStates.SingleOrDefaultAsync(entry => entry.MailFolderId == folderId, cancellationToken);
        if (state is null)
            return category == SyncFailureCategory.Transient ? SyncFailureOutcome.RetryTransient : SyncFailureOutcome.Recorded;

        state.ConsecutiveFailures++;
        state.LastFailureCategory = category;
        state.LastFailureAt = _clock.UtcNow;

        if (category == SyncFailureCategory.Transient)
        {
            var retry = SyncRetryPolicy.FromSettings(
                settings.Sync.TransientRetryMaxAttempts,
                settings.Sync.RetryBaseDelaySeconds,
                settings.Sync.RetryMaxDelaySeconds,
                _clock,
                _jitterSource);
            state.NextRetryAt = retry.NextRetryAt(state.ConsecutiveFailures);
        }
        else
        {
            state.NextRetryAt = null;
        }

        var shouldNotify = state.ConsecutiveFailures >= settings.Sync.SyncErrorFailureThreshold
            && state.LastErrorNotifiedAt is null;
        await db.SaveChangesAsync(cancellationToken);

        if (shouldNotify)
        {
            try
            {
                var push = provider.GetRequiredService<IPushNotificationService>();
                await push.NotifyAsync(new PushEvent(PushEventType.SyncError, item.Request.AccountId, FolderId: folderId), cancellationToken);
                state.LastErrorNotifiedAt = state.LastFailureAt;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception pushException)
            {
                _logger.LogWarning(pushException, "Sync error notification failed. Sync state is unaffected.");
            }
        }

        _logger.LogWarning(
            "Mail sync failed ({FailureCategory}, origin {Origin}, folder type {FolderType}, attempt {Attempts}).",
            MailClientTelemetry.FailureCategory(category),
            MailClientTelemetry.Origin(item.Origin),
            MailClientTelemetry.FolderType(folder.FolderType),
            state.ConsecutiveFailures);
        return category == SyncFailureCategory.Transient ? SyncFailureOutcome.RetryTransient : SyncFailureOutcome.Recorded;
    }

    internal static SyncFailureCategory MapFailure(Exception exception)
    {
        if (exception is MailConnectionException connection)
        {
            return connection.Failure switch
            {
                MailConnectionFailure.Network => SyncFailureCategory.Transient,
                MailConnectionFailure.Authentication => SyncFailureCategory.Authentication,
                MailConnectionFailure.Tls => SyncFailureCategory.Configuration,
                MailConnectionFailure.Protocol => MapProtocolFailure(connection),
                _ => SyncFailureCategory.Permanent
            };
        }

        return SyncFailureClassifier.Classify(exception);
    }

    private static SyncFailureCategory MapProtocolFailure(MailConnectionException exception)
    {
        var message = (exception.InnerException?.Message ?? exception.Message).ToLowerInvariant();
        if (message.Contains("timeout") || message.Contains("temporar") || message.Contains("unavailable") || message.Contains("try again"))
            return SyncFailureCategory.Transient;
        return SyncFailureCategory.Permanent;
    }

    private static Task<int> ConsecutiveFailuresAsync(AppDbContext db, Guid folderId, CancellationToken cancellationToken) =>
        db.SyncStates.Where(entry => entry.MailFolderId == folderId)
            .Select(entry => entry.ConsecutiveFailures)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task RecoverPendingReconciliationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = await db.Mails
                .Where(mail => mail.ReconciliationState == MailReconciliationState.Pending && mail.ExpectedMailFolderId != null)
                .Select(mail => new { mail.MailAccountId, FolderId = mail.ExpectedMailFolderId!.Value })
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var request in pending)
            {
                Enqueue(SyncScheduling.ForReconciliationFolder(request.MailAccountId, request.FolderId, _queue.NextSequence()), newWork: true);
            }

            if (pending.Count > 0)
                _signals.Writer.TryWrite(new SyncScheduleSignal());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup reconciliation recovery failed.");
        }
    }

    private async Task WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _signals.Reader.ReadAsync(timeoutSource.Token);
            while (_signals.Reader.TryRead(out _))
            {
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed record FolderTarget(Guid FolderId, MailFolderType FolderType);
    private sealed record SyncScheduleSignal;
    private enum SyncFailureOutcome { RetryTransient, Recorded, Authentication }
}
