using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using MailClient.Infrastructure.Security;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Sync;

/// <summary>Static deployment configuration (<c>Sync:Idle</c>) for <see cref="ImapIdleWatcher"/>.</summary>
public sealed class ImapIdleOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Upper bound on simultaneously held IDLE connections; accounts beyond it keep using polling.</summary>
    public int MaxConnections { get; set; } = 100;
}

/// <summary>
/// Holds one IMAP IDLE connection on the INBOX of each active account and schedules an inbox sync as soon as
/// the server reports a change, instead of waiting for the next poll. Polling stays in place as the safety net,
/// so accounts whose server lacks IDLE, or that exceed <see cref="ImapIdleOptions.MaxConnections"/>, behave as before.
/// </summary>
public sealed class ImapIdleWatcher(
    IServiceScopeFactory scopes,
    MailConnectionHelper connections,
    ISyncScheduler scheduler,
    ImapIdleOptions options,
    ILogger<ImapIdleWatcher> logger) : BackgroundService
{
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(60);
    // RFC 2177: servers may drop a connection idling for 30 minutes; renew well before that.
    internal static readonly TimeSpan IdleRenewal = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan HardStopGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnsupportedRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Dictionary<Guid, (CancellationTokenSource Cancel, Task Task)> _watches = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "IMAP IDLE reconciliation failed.");
                }

                await Task.Delay(ReconcileInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var (cancel, _) in _watches.Values)
                cancel.Cancel();
            try
            {
                await Task.WhenAll(_watches.Values.Select(watch => watch.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "IMAP IDLE watchers did not stop cleanly.");
            }
            foreach (var (cancel, _) in _watches.Values)
                cancel.Dispose();
            _watches.Clear();
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        Dictionary<Guid, Guid> wanted = [];
        await using (var scope = scopes.CreateAsyncScope())
        {
            var snapshot = await scope.ServiceProvider.GetRequiredService<RuntimeOperationSettings>().GetAsync(cancellationToken);
            if (snapshot.Settings.Sync.Enabled)
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var inboxes = await db.MailFolders.AsNoTracking()
                    .Where(folder => folder.FolderType == MailFolderType.Inbox
                        && folder.IsSyncEnabled
                        && folder.IsAvailable
                        && folder.MailAccount!.Status == MailAccountStatus.Active)
                    .OrderBy(folder => folder.MailAccountId)
                    .Take(Math.Max(0, options.MaxConnections))
                    .Select(folder => new { folder.MailAccountId, folder.Id })
                    .ToListAsync(cancellationToken);
                wanted = inboxes.ToDictionary(item => item.MailAccountId, item => item.Id);
            }
        }

        foreach (var accountId in _watches.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _watches[accountId].Cancel.Cancel();
            _watches.Remove(accountId);
        }

        foreach (var (accountId, inboxId) in wanted.Where(item => !_watches.ContainsKey(item.Key)))
        {
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _watches[accountId] = (cancel, Task.Run(() => WatchAccountAsync(accountId, inboxId, cancel.Token), CancellationToken.None));
        }
    }

    private async Task WatchAccountAsync(Guid accountId, Guid inboxId, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = TimeSpan.Zero;
            try
            {
                ResolvedCredential resolved;
                await using (var scope = scopes.CreateAsyncScope())
                    resolved = await scope.ServiceProvider.GetRequiredService<MailCredentialResolver>()
                        .ResolveAsync(accountId, cancellationToken);
                var endpoint = new MailServerEndpoint(resolved.Account.ImapHost, resolved.Account.ImapPort, resolved.Account.ImapSecurity);
                var supported = await connections.WithImapAsync(
                    endpoint,
                    resolved.Username,
                    resolved.Secret,
                    "Idle",
                    (client, ct) => IdleAsync(
                        client,
                        token => ScheduleInboxSyncAsync(accountId, inboxId, token),
                        IdleRenewal,
                        ct),
                    cancellationToken,
                    resolved.AuthenticationMethod);
                if (!supported)
                {
                    logger.LogInformation("IMAP server of account {AccountId} does not support IDLE; polling only.", accountId);
                    delay = UnsupportedRetry;
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                delay = Backoff(failures);
                logger.LogWarning(ex, "IMAP IDLE for account {AccountId} ended (attempt {Attempt}); retrying in {Delay}.",
                    accountId, failures, delay);
            }

            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ScheduleInboxSyncAsync(Guid accountId, Guid inboxId, CancellationToken cancellationToken)
    {
        try
        {
            await scheduler.ScheduleFolderAsync(accountId, inboxId, SyncOrigin.Periodic, cancellationToken);
        }
        catch (SyncQueueFullException)
        {
            // The periodic poll is still the safety net; a full queue must not drop the IDLE connection.
            logger.LogDebug("Sync queue full; IDLE change for account {AccountId} left to the next poll.", accountId);
        }
    }

    internal static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, Math.Clamp(failures - 1, 0, 10))));

    /// <summary>
    /// Idles on INBOX and invokes <paramref name="onChange"/> once after connecting (to catch up on anything missed
    /// while disconnected) and again after every server-reported count change. Returns <c>false</c> when the server
    /// has no IDLE capability; otherwise runs until cancelled or the connection fails.
    /// </summary>
    internal static async Task<bool> IdleAsync(
        ImapClient client,
        Func<CancellationToken, Task> onChange,
        TimeSpan renewal,
        CancellationToken cancellationToken)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
            return false;
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        await onChange(cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Cancellation reaches the connection only through the done token (a graceful DONE); the hard stop is a
            // separate timer so a dead connection cannot hold the loop forever. Cancelling the connection first
            // would tear down TLS mid-IDLE.
            using var hardStop = new CancellationTokenSource(renewal + HardStopGrace);
            using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            done.CancelAfter(renewal);
            var changed = 0;
            void OnChanged(object? sender, EventArgs args)
            {
                Interlocked.Exchange(ref changed, 1);
                done.Cancel();
            }

            inbox.CountChanged += OnChanged;
            try
            {
                await client.IdleAsync(done.Token, hardStop.Token);
            }
            finally
            {
                inbox.CountChanged -= OnChanged;
            }

            if (Volatile.Read(ref changed) == 1)
                await onChange(cancellationToken);
        }
    }
}
