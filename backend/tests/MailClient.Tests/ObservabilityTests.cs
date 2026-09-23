using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Formatting.Json;

namespace MailClient.Tests;

internal sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
{
    public bool Has(string key, object value) => Tags.TryGetValue(key, out var actual) && Equals(actual, value);
}

/// <summary>
/// Captures measurements from one private MailClientMetrics instance only, so parallel tests never interfere.
/// </summary>
internal sealed class MetricsCapture : IDisposable
{
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();

    public MetricsCapture(SyncScheduleQueue? queue = null)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, _factory))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
        Metrics = new MailClientMetrics(_factory, queue);
    }

    public MailClientMetrics Metrics { get; }

    public IReadOnlyList<RecordedMeasurement> All
    {
        get
        {
            _listener.RecordObservableInstruments();
            return _measurements.ToList();
        }
    }

    public IReadOnlyList<RecordedMeasurement> For(string instrument) => All.Where(item => item.Instrument == instrument).ToList();

    public double Sum(string instrument, params (string Key, object Value)[] tags) =>
        For(instrument).Where(item => tags.All(tag => item.Has(tag.Key, tag.Value))).Sum(item => item.Value);

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _measurements.Enqueue(new RecordedMeasurement(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));

    public void Dispose()
    {
        _listener.Dispose();
        _factory.Dispose();
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }
}

internal sealed class StubSyncLockProvider(params SyncLockStatus[] outcomes) : ISyncLockProvider
{
    private int _calls;
    public int Calls => _calls;

    public Task<SyncLockAcquisition> TryAcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken)
    {
        var index = Math.Min(Interlocked.Increment(ref _calls) - 1, outcomes.Length - 1);
        return Task.FromResult(outcomes[index] switch
        {
            SyncLockStatus.Acquired => SyncLockAcquisition.Acquired(new NoOpLock()),
            SyncLockStatus.Contended => SyncLockAcquisition.Contended,
            _ => SyncLockAcquisition.InfrastructureFailure
        });
    }

    private sealed class NoOpLock : ISyncLock
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[CollectionDefinition("telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("telemetry")]
public sealed class ObservabilityTests
{
    private static readonly HashSet<string> AllowedMetricTags =
    [
        "origin", "reason", "folder_type", "failure_category", "result", "purpose", "provider",
        "has_text_query", "event_type", "discovery_source", "protocol"
    ];

    private static readonly HashSet<string> AllowedSpanTags =
    [
        "sync.origin", "sync.result", "sync.failure_category", "mail.provider", "mail.folder_type", "error.type"
    ];

    [Fact]
    public void Metrics_UseOnlyBoundedLabels()
    {
        using var capture = new MetricsCapture(new SyncScheduleQueue(10));
        var metrics = capture.Metrics;

        metrics.RecordSyncScheduled(SyncOrigin.UserRequested);
        metrics.RecordSyncQueueRejected(SyncOrigin.Periodic, "queue_full");
        metrics.RecordSyncFolderCompleted(SyncOrigin.Periodic, MailFolderType.Inbox, TimeSpan.FromSeconds(1), recovered: true);
        metrics.RecordSyncFolderFailed(SyncOrigin.Initial, MailFolderType.Custom, SyncFailureCategory.Transient, TimeSpan.FromSeconds(1));
        metrics.RecordSyncFolderCancelled(SyncOrigin.Reconciliation, MailFolderType.Sent, TimeSpan.FromSeconds(1));
        metrics.RecordSyncRetryScheduled(SyncOrigin.Periodic);
        metrics.RecordSyncRetriesExhausted(SyncOrigin.Periodic);
        metrics.AddActiveSyncAccounts(1);
        metrics.AddActiveSyncFolders(1);
        metrics.RecordSyncConnectionBudgetWait(TimeSpan.FromMilliseconds(5));
        metrics.RecordLockAcquisition("account_sync", "contended");
        metrics.RecordOAuthAuthorizationStarted(MailProvider.Google);
        metrics.RecordOAuthAuthorizationCompleted(MailProvider.Microsoft, succeeded: false);
        metrics.RecordOAuthTokenRefresh(MailProvider.Google, "success", TimeSpan.FromSeconds(1));
        metrics.RecordOAuthReauthenticationRequired(MailProvider.Google);
        metrics.RecordMailSend(MailProvider.Custom, "failure", TimeSpan.FromSeconds(1));
        metrics.RecordSentCopyFailure(MailProvider.ICloud);
        metrics.RecordMailSearch(hasTextQuery: true, "success", TimeSpan.FromSeconds(1), 3);
        metrics.RecordPushDelivery(PushEventType.NewMail, 1, 1, 1, TimeSpan.FromSeconds(1));
        metrics.RecordPushFailure(PushEventType.SyncError);
        metrics.RecordDiscovery("found", DiscoverySource.Autoconfig, MailProvider.Yahoo, TimeSpan.FromSeconds(1));
        metrics.RecordMailConnectionFailure("imap", MailConnectionFailure.Tls);
        metrics.RecordRuntimeSettingsLoadFailure();

        var measurements = capture.All;
        Assert.Contains(measurements, item => item.Instrument == "mailclient.sync.queue.pending");
        Assert.All(measurements, item =>
        {
            Assert.StartsWith("mailclient.", item.Instrument, StringComparison.Ordinal);
            Assert.All(item.Tags, tag =>
            {
                Assert.Contains(tag.Key, AllowedMetricTags);
                var value = Convert.ToString(tag.Value)!;
                Assert.False(Guid.TryParse(value, out _), $"{item.Instrument}.{tag.Key} carries an identifier.");
                Assert.DoesNotContain("@", value);
                Assert.DoesNotContain(".", value);
            });
        });
    }

    [Fact]
    public async Task BackgroundSync_CreatesRootTraceWithFolderChildSpan_AndBoundedTagsOnly()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = SampleMailClientActivities(stopped);
        var observed = new ConcurrentQueue<Activity?>();
        var harness = SyncCoordinatorTests.CreateHarness(new ActivityObservingExecutor(observed), maxAccounts: 1, maxFolders: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);

        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        var folderSpan = Assert.Single(observed);
        Assert.NotNull(folderSpan);
        Assert.Equal("mailclient.sync.folder", folderSpan.OperationName);
        var accountSpan = folderSpan.Parent;
        Assert.NotNull(accountSpan);
        Assert.Equal("mailclient.sync.account", accountSpan.OperationName);
        Assert.Null(accountSpan.Parent);
        Assert.Equal(default, accountSpan.ParentSpanId);
        Assert.Equal(accountSpan.TraceId, folderSpan.TraceId);
        Assert.Contains(folderSpan.TagObjects, tag => tag is { Key: "mail.folder_type", Value: "inbox" });
        Assert.Contains(folderSpan.TagObjects, tag => tag is { Key: "sync.result", Value: "success" });
        AssertBoundedSpanTags([accountSpan, folderSpan], accountId, folderId);
    }

    [Fact]
    public async Task SyncFailureSpan_ParticipatesInAmbientTrace_WithoutSensitiveValues()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = SampleMailClientActivities(stopped);
        var failure = new MailConnectionException(MailConnectionFailure.Tls, "certificate for imap.private-host.example rejected");
        var harness = SyncCoordinatorTests.CreateHarness(new SyncCoordinatorTests.FailingExecutor(failure), maxAccounts: 1, maxFolders: 1);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Custom);

        using var root = new Activity("test-root").Start();
        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.UserRequested, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        var spans = stopped.Where(activity => activity.TraceId == root.TraceId).ToList();
        var accountSpan = Assert.Single(spans, span => span.OperationName == "mailclient.sync.account");
        var folderSpan = Assert.Single(spans, span => span.OperationName == "mailclient.sync.folder");
        Assert.Equal(root.SpanId, accountSpan.ParentSpanId);
        Assert.Equal(accountSpan.SpanId, folderSpan.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Error, folderSpan.Status);
        Assert.Contains(folderSpan.TagObjects, tag => tag is { Key: "sync.failure_category", Value: "configuration" });
        Assert.Null(folderSpan.StatusDescription);
        Assert.Empty(folderSpan.Events);
        AssertBoundedSpanTags(spans, accountId, folderId);
    }

    [Fact]
    public async Task SyncWithoutTraceListener_BehavesNormally()
    {
        using var capture = new MetricsCapture();
        var executor = new SyncCoordinatorTests.OrderRecordingExecutor(new ConcurrentQueue<Guid>());
        var harness = SyncCoordinatorTests.CreateHarness(executor, maxAccounts: 1, maxFolders: 1, metrics: capture.Metrics);
        var accountId = await harness.SeedAccountAsync(0);
        var folderId = await harness.AddFolderAsync(accountId, MailFolderType.Inbox);

        await harness.Scheduler.ScheduleFolderAsync(accountId, folderId, SyncOrigin.Periodic, CancellationToken.None);
        await harness.Scheduler.DispatchOnceAsync(CancellationToken.None);

        Assert.Single(executor.FolderCalls);
        Assert.Equal(1, capture.Sum("mailclient.sync.completed"));
    }

    [Fact]
    public void JsonLogs_IncludeTraceAndSpanIdsFromCurrentActivity()
    {
        var output = new StringWriter();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new JsonWriterSink(output)).CreateLogger();

        using (var activity = new Activity("background-work").Start())
        {
            logger.Information("inside background work");
            Assert.Contains($"\"TraceId\":\"{activity.TraceId}\"", output.ToString());
            Assert.Contains($"\"SpanId\":\"{activity.SpanId}\"", output.ToString());
        }
    }

    [Fact]
    public async Task Search_RecordsOperationAndDurationWithoutQueryContent()
    {
        using var capture = new MetricsCapture();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var accountId = Guid.NewGuid();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation(), capture.Metrics);

        var response = await service.SearchAsync(
            accountId,
            new MailSearchRequest("confidential-term", Guid.NewGuid(), null, "sender@example.test", null, null, null, null, null, null, 1, 20),
            CancellationToken.None);

        Assert.Equal(0, response.Total);
        Assert.Equal(1, capture.Sum("mailclient.mail.search", ("has_text_query", true), ("result", "success")));
        Assert.Single(capture.For("mailclient.mail.search.duration"));
        Assert.Single(capture.For("mailclient.mail.search.result_count"));
        Assert.DoesNotContain(capture.All.SelectMany(item => item.Tags.Values), value =>
            Convert.ToString(value)!.Contains("confidential", StringComparison.Ordinal) || Convert.ToString(value)!.Contains("sender", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LockMetrics_DistinguishContentionFromInfrastructureFailure()
    {
        using var capture = new MetricsCapture();
        var accountId = Guid.NewGuid();
        var memory = new InMemorySyncLockProvider(capture.Metrics);
        var unreachable = new PostgresSyncLockProvider(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2",
            NullLogger<PostgresSyncLockProvider>.Instance,
            capture.Metrics);

        await using var held = await memory.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, CancellationToken.None);
        await using var contended = await memory.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, CancellationToken.None);
        await using var failed = await unreachable.TryAcquireAsync(accountId, SyncLockPurpose.OAuthRefresh, CancellationToken.None);

        Assert.Equal(SyncLockStatus.Acquired, held.Status);
        Assert.Equal(SyncLockStatus.Contended, contended.Status);
        Assert.Equal(SyncLockStatus.InfrastructureFailure, failed.Status);
        Assert.Equal(1, capture.Sum("mailclient.lock.acquisitions", ("purpose", "account_sync"), ("result", "acquired")));
        Assert.Equal(1, capture.Sum("mailclient.lock.acquisitions", ("purpose", "account_sync"), ("result", "contended")));
        Assert.Equal(1, capture.Sum("mailclient.lock.acquisitions", ("purpose", "oauth_refresh"), ("result", "failed")));
    }

    private static ActivityListener SampleMailClientActivities(ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MailClientTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void AssertBoundedSpanTags(IEnumerable<Activity> spans, Guid accountId, Guid folderId)
    {
        foreach (var tag in spans.SelectMany(span => span.TagObjects))
        {
            Assert.Contains(tag.Key, AllowedSpanTags);
            var value = Convert.ToString(tag.Value)!;
            Assert.DoesNotContain(accountId.ToString(), value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(folderId.ToString(), value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@", value);
            Assert.DoesNotContain("private-host", value);
        }
    }

    private sealed class JsonWriterSink(TextWriter output) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => new JsonFormatter().Format(logEvent, output);
    }

    private sealed class ActivityObservingExecutor(ConcurrentQueue<Activity?> observed) : ISyncExecutor
    {

        public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            observed.Enqueue(Activity.Current);
            return Task.CompletedTask;
        }
    }
}
