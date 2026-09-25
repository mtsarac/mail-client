using System.Diagnostics;
using System.Diagnostics.Metrics;
using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Enums;

namespace MailClient.Application.Observability;

/// <summary>
/// Shared telemetry names and bounded tag values. Metric and span attributes must never carry
/// account, mail, folder or conversation identifiers, addresses, hosts, subjects, tokens or exception text.
/// </summary>
public static class MailClientTelemetry
{
    public const string MeterName = "MailClient";
    public const string ActivitySourceName = "MailClient";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static Activity? StartActivity(string name) => ActivitySource.StartActivity(name, ActivityKind.Internal);

    public static void MarkFailed(Activity? activity, Exception exception)
    {
        if (activity is null)
            return;
        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag("error.type", exception.GetType().FullName);
    }

    public static string Origin(SyncOrigin origin) => origin switch
    {
        SyncOrigin.Initial => "initial",
        SyncOrigin.UserRequested => "user",
        SyncOrigin.Reconciliation => "reconciliation",
        SyncOrigin.Periodic => "periodic",
        _ => "unknown"
    };

    public static string Provider(MailProvider provider) => provider switch
    {
        MailProvider.Google => "google",
        MailProvider.Microsoft => "microsoft",
        MailProvider.ICloud => "icloud",
        MailProvider.Yahoo => "yahoo",
        _ => "custom"
    };

    public static string FolderType(MailFolderType folderType) => folderType switch
    {
        MailFolderType.Inbox => "inbox",
        MailFolderType.Sent => "sent",
        MailFolderType.Drafts => "drafts",
        MailFolderType.Trash => "trash",
        MailFolderType.Junk => "junk",
        MailFolderType.Archive => "archive",
        MailFolderType.Custom => "custom",
        _ => "unknown"
    };

    public static string FailureCategory(SyncFailureCategory category) => category switch
    {
        SyncFailureCategory.Transient => "transient",
        SyncFailureCategory.Authentication => "authentication",
        SyncFailureCategory.Configuration => "configuration",
        _ => "permanent"
    };

    public static string DiscoverySourceName(DiscoverySource source) => source switch
    {
        DiscoverySource.KnownProvider => "known_provider",
        DiscoverySource.DnsSrv => "dns_srv",
        DiscoverySource.Autoconfig => "autoconfig",
        DiscoverySource.Autodiscover => "autodiscover",
        DiscoverySource.Heuristic => "heuristic",
        _ => "manual"
    };

    public static string PushEventTypeName(PushEventType type) => type switch
    {
        PushEventType.NewMail => "new_mail",
        PushEventType.MailStateChanged => "mail_state_changed",
        PushEventType.AccountReauthenticationRequired => "account_reauthentication_required",
        PushEventType.SyncError => "sync_error",
        PushEventType.SnoozeExpired => "snooze_expired",
        PushEventType.ReplyReminder => "reply_reminder",
        _ => "unknown"
    };

    public static string ConnectionFailure(MailConnectionFailure failure) => failure switch
    {
        MailConnectionFailure.Authentication => "authentication",
        MailConnectionFailure.Tls => "tls",
        MailConnectionFailure.Protocol => "protocol",
        _ => "network"
    };
}

/// <summary>
/// The single MailClient meter. All custom instruments live here so tag cardinality stays reviewable in one place.
/// </summary>
public sealed class MailClientMetrics
{
    private static readonly double[] DurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300];
    private static readonly int[] CountBuckets = [0, 1, 5, 10, 25, 50, 100, 250, 500, 1000];

    private readonly Counter<long> _syncScheduled;
    private readonly Counter<long> _syncQueueRejected;
    private readonly Counter<long> _syncCompleted;
    private readonly Counter<long> _syncFailures;
    private readonly Counter<long> _syncRetries;
    private readonly Counter<long> _syncRetriesExhausted;
    private readonly Counter<long> _syncRecoveries;
    private readonly Histogram<double> _syncDuration;
    private readonly UpDownCounter<long> _syncActiveAccounts;
    private readonly UpDownCounter<long> _syncActiveFolders;
    private readonly Histogram<double> _syncConnectionBudgetWait;
    private readonly Counter<long> _lockAcquisitions;
    private readonly Counter<long> _oauthAuthorizationStarted;
    private readonly Counter<long> _oauthAuthorizationCompleted;
    private readonly Counter<long> _oauthTokenRefresh;
    private readonly Histogram<double> _oauthTokenRefreshDuration;
    private readonly Counter<long> _oauthReauthenticationRequired;
    private readonly Counter<long> _mailSend;
    private readonly Histogram<double> _mailSendDuration;
    private readonly Counter<long> _mailSentCopyFailures;
    private readonly Counter<long> _mailSearch;
    private readonly Histogram<double> _mailSearchDuration;
    private readonly Histogram<int> _mailSearchResultCount;
    private readonly Counter<long> _pushNotifications;
    private readonly Counter<long> _pushFailures;
    private readonly Counter<long> _pushInvalidTokens;
    private readonly Histogram<double> _pushDuration;
    private readonly Counter<long> _discovery;
    private readonly Histogram<double> _discoveryDuration;
    private readonly Counter<long> _mailConnectionFailures;
    private readonly Counter<long> _runtimeSettingsLoadFailures;

    public MailClientMetrics(IMeterFactory meterFactory, SyncScheduleQueue? syncQueue = null)
    {
        var meter = meterFactory.Create(MailClientTelemetry.MeterName);
        var durationAdvice = new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets };

        _syncScheduled = meter.CreateCounter<long>("mailclient.sync.scheduled", "{request}", "Sync requests accepted by the scheduler.");
        _syncQueueRejected = meter.CreateCounter<long>("mailclient.sync.queue.rejected", "{request}", "Sync requests rejected or shed because the queue was full.");
        _syncCompleted = meter.CreateCounter<long>("mailclient.sync.completed", "{folder}", "Folder synchronizations that completed successfully.");
        _syncFailures = meter.CreateCounter<long>("mailclient.sync.failures", "{folder}", "Folder synchronizations that failed, by failure category.");
        _syncRetries = meter.CreateCounter<long>("mailclient.sync.retries", "{retry}", "Transient sync retries scheduled.");
        _syncRetriesExhausted = meter.CreateCounter<long>("mailclient.sync.retries.exhausted", "{episode}", "Transient failure episodes that exhausted retry attempts.");
        _syncRecoveries = meter.CreateCounter<long>("mailclient.sync.recoveries", "{folder}", "Successful folder synchronizations after previous consecutive failures.");
        _syncDuration = meter.CreateHistogram("mailclient.sync.duration", "s", "Folder synchronization duration.", advice: durationAdvice);
        _syncActiveAccounts = meter.CreateUpDownCounter<long>("mailclient.sync.active.accounts", "{account}", "Accounts currently executing sync work.");
        _syncActiveFolders = meter.CreateUpDownCounter<long>("mailclient.sync.active.folders", "{folder}", "Folders currently executing sync work.");
        _syncConnectionBudgetWait = meter.CreateHistogram("mailclient.sync.connection_budget.wait", "s", "Time spent waiting for a per-host sync connection slot.", advice: durationAdvice);
        _lockAcquisitions = meter.CreateCounter<long>("mailclient.lock.acquisitions", "{attempt}", "Distributed lock acquisition attempts by purpose and result.");
        _oauthAuthorizationStarted = meter.CreateCounter<long>("mailclient.oauth.authorization.started", "{authorization}", "OAuth authorization flows started.");
        _oauthAuthorizationCompleted = meter.CreateCounter<long>("mailclient.oauth.authorization.completed", "{authorization}", "OAuth authorization flows completed.");
        _oauthTokenRefresh = meter.CreateCounter<long>("mailclient.oauth.token.refresh", "{refresh}", "OAuth provider token refresh attempts.");
        _oauthTokenRefreshDuration = meter.CreateHistogram("mailclient.oauth.token.refresh.duration", "s", "OAuth provider token refresh duration.", advice: durationAdvice);
        _oauthReauthenticationRequired = meter.CreateCounter<long>("mailclient.oauth.reauthentication.required", "{account}", "Accounts transitioned to requiring reauthentication.");
        _mailSend = meter.CreateCounter<long>("mailclient.mail.send", "{message}", "SMTP send attempts by result.");
        _mailSendDuration = meter.CreateHistogram("mailclient.mail.send.duration", "s", "SMTP send duration.", advice: durationAdvice);
        _mailSentCopyFailures = meter.CreateCounter<long>("mailclient.mail.sent_copy.failures", "{message}", "Sent messages whose Sent-folder copy could not be stored.");
        _mailSearch = meter.CreateCounter<long>("mailclient.mail.search", "{search}", "Mail searches executed.");
        _mailSearchDuration = meter.CreateHistogram("mailclient.mail.search.duration", "s", "Mail search duration.", advice: durationAdvice);
        _mailSearchResultCount = meter.CreateHistogram("mailclient.mail.search.result_count", "{mail}", "Total matching mails per search.", advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = CountBuckets });
        _pushNotifications = meter.CreateCounter<long>("mailclient.push.notifications", "{notification}", "Push messages delivered to devices by result.");
        _pushFailures = meter.CreateCounter<long>("mailclient.push.failures", "{delivery}", "Push delivery attempts that failed as a whole.");
        _pushInvalidTokens = meter.CreateCounter<long>("mailclient.push.invalid_tokens", "{token}", "Invalid device tokens removed after delivery.");
        _pushDuration = meter.CreateHistogram("mailclient.push.duration", "s", "Push gateway delivery duration.", advice: durationAdvice);
        _discovery = meter.CreateCounter<long>("mailclient.discovery", "{discovery}", "Mail server discovery attempts by result.");
        _discoveryDuration = meter.CreateHistogram("mailclient.discovery.duration", "s", "Mail server discovery duration.", advice: durationAdvice);
        _mailConnectionFailures = meter.CreateCounter<long>("mailclient.mail.connection.failures", "{failure}", "Remote IMAP/SMTP connection or protocol failures.");
        _runtimeSettingsLoadFailures = meter.CreateCounter<long>("mailclient.runtime_settings.load_failures", "{failure}", "Persisted runtime settings that could not be read or validated.");

        if (syncQueue is not null)
            meter.CreateObservableGauge("mailclient.sync.queue.pending", () => syncQueue.Count, "{request}", "Sync requests waiting in the scheduler queue.");
    }

    public void RecordSyncScheduled(SyncOrigin origin) =>
        _syncScheduled.Add(1, new TagList { { "origin", MailClientTelemetry.Origin(origin) } });

    public void RecordSyncQueueRejected(SyncOrigin origin, string reason) =>
        _syncQueueRejected.Add(1, new TagList { { "origin", MailClientTelemetry.Origin(origin) }, { "reason", reason } });

    public void RecordSyncFolderCompleted(SyncOrigin origin, MailFolderType folderType, TimeSpan duration, bool recovered)
    {
        var tags = FolderTags(origin, folderType);
        _syncCompleted.Add(1, tags);
        if (recovered)
            _syncRecoveries.Add(1, tags);
        tags.Add("result", "success");
        _syncDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordSyncFolderFailed(SyncOrigin origin, MailFolderType folderType, SyncFailureCategory category, TimeSpan duration)
    {
        var tags = FolderTags(origin, folderType);
        tags.Add("failure_category", MailClientTelemetry.FailureCategory(category));
        _syncFailures.Add(1, tags);
        var durationTags = FolderTags(origin, folderType);
        durationTags.Add("result", "failure");
        _syncDuration.Record(duration.TotalSeconds, durationTags);
    }

    public void RecordSyncFolderCancelled(SyncOrigin origin, MailFolderType folderType, TimeSpan duration)
    {
        var tags = FolderTags(origin, folderType);
        tags.Add("result", "cancelled");
        _syncDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordSyncRetryScheduled(SyncOrigin origin) =>
        _syncRetries.Add(1, new TagList { { "origin", MailClientTelemetry.Origin(origin) }, { "failure_category", "transient" } });

    public void RecordSyncRetriesExhausted(SyncOrigin origin) =>
        _syncRetriesExhausted.Add(1, new TagList { { "origin", MailClientTelemetry.Origin(origin) } });

    public void AddActiveSyncAccounts(int delta) => _syncActiveAccounts.Add(delta);

    public void AddActiveSyncFolders(int delta) => _syncActiveFolders.Add(delta);

    public void RecordSyncConnectionBudgetWait(TimeSpan wait) => _syncConnectionBudgetWait.Record(wait.TotalSeconds);

    /// <param name="purpose">account_sync or oauth_refresh.</param>
    /// <param name="result">acquired, contended or failed.</param>
    public void RecordLockAcquisition(string purpose, string result) =>
        _lockAcquisitions.Add(1, new TagList { { "purpose", purpose }, { "result", result } });

    public void RecordOAuthAuthorizationStarted(MailProvider provider) =>
        _oauthAuthorizationStarted.Add(1, ProviderTags(provider));

    public void RecordOAuthAuthorizationCompleted(MailProvider provider, bool succeeded)
    {
        var tags = ProviderTags(provider);
        tags.Add("result", succeeded ? "success" : "failure");
        _oauthAuthorizationCompleted.Add(1, tags);
    }

    /// <param name="result">success, failure or reauthentication_required.</param>
    public void RecordOAuthTokenRefresh(MailProvider provider, string result, TimeSpan duration)
    {
        var tags = ProviderTags(provider);
        tags.Add("result", result);
        _oauthTokenRefresh.Add(1, tags);
        _oauthTokenRefreshDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordOAuthReauthenticationRequired(MailProvider provider) =>
        _oauthReauthenticationRequired.Add(1, ProviderTags(provider));

    /// <param name="result">success, failure, delivery_unknown or cancelled.</param>
    public void RecordMailSend(MailProvider provider, string result, TimeSpan duration)
    {
        var tags = ProviderTags(provider);
        tags.Add("result", result);
        _mailSend.Add(1, tags);
        _mailSendDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordSentCopyFailure(MailProvider provider) =>
        _mailSentCopyFailures.Add(1, ProviderTags(provider));

    /// <param name="result">success, failure or cancelled.</param>
    public void RecordMailSearch(bool hasTextQuery, string result, TimeSpan duration, int? resultCount)
    {
        var tags = new TagList { { "has_text_query", hasTextQuery }, { "result", result } };
        _mailSearch.Add(1, tags);
        _mailSearchDuration.Record(duration.TotalSeconds, tags);
        if (resultCount is { } count)
            _mailSearchResultCount.Record(count, new TagList { { "has_text_query", hasTextQuery } });
    }

    public void RecordPushDelivery(PushEventType type, int succeeded, int failed, int invalidTokens, TimeSpan duration)
    {
        var eventType = MailClientTelemetry.PushEventTypeName(type);
        if (succeeded > 0)
            _pushNotifications.Add(succeeded, new TagList { { "event_type", eventType }, { "result", "success" } });
        if (failed > 0)
            _pushNotifications.Add(failed, new TagList { { "event_type", eventType }, { "result", "failure" } });
        if (invalidTokens > 0)
            _pushInvalidTokens.Add(invalidTokens, new TagList { { "event_type", eventType } });
        _pushDuration.Record(duration.TotalSeconds, new TagList { { "event_type", eventType } });
    }

    public void RecordPushFailure(PushEventType type) =>
        _pushFailures.Add(1, new TagList { { "event_type", MailClientTelemetry.PushEventTypeName(type) } });

    /// <param name="result">found, not_found, failure or cancelled.</param>
    public void RecordDiscovery(string result, DiscoverySource? source, MailProvider? provider, TimeSpan duration)
    {
        var tags = new TagList { { "result", result } };
        if (source is { } discoverySource)
            tags.Add("discovery_source", MailClientTelemetry.DiscoverySourceName(discoverySource));
        if (provider is { } mailProvider)
            tags.Add("provider", MailClientTelemetry.Provider(mailProvider));
        _discovery.Add(1, tags);
        _discoveryDuration.Record(duration.TotalSeconds, new TagList { { "result", result } });
    }

    /// <param name="protocol">imap or smtp.</param>
    public void RecordMailConnectionFailure(string protocol, MailConnectionFailure failure) =>
        _mailConnectionFailures.Add(1, new TagList { { "protocol", protocol }, { "failure_category", MailClientTelemetry.ConnectionFailure(failure) } });

    public void RecordRuntimeSettingsLoadFailure() => _runtimeSettingsLoadFailures.Add(1);

    private static TagList FolderTags(SyncOrigin origin, MailFolderType folderType) =>
        new() { { "origin", MailClientTelemetry.Origin(origin) }, { "folder_type", MailClientTelemetry.FolderType(folderType) } };

    private static TagList ProviderTags(MailProvider provider) =>
        new() { { "provider", MailClientTelemetry.Provider(provider) } };
}
