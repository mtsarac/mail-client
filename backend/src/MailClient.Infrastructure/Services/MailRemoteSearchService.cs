using MailClient.Infrastructure.Runtime;
using MailFolder = MailClient.Domain.Entities.MailFolder;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Sync;
using MailKit;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed class MailRemoteSearchService
{
    private const int ImportBudget = 25;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly AppDbContext _db;
    private readonly MailCredentialResolver _credentials;
    private readonly MailConnectionHelper _connections;
    private readonly MailFolderSyncService _sync;
    private readonly RuntimeOperationSettings _operationSettings;
    private readonly ISyncLockProvider _locks;
    private readonly TimeSpan _deadline;

    public MailRemoteSearchService(
        AppDbContext db,
        MailCredentialResolver credentials,
        MailConnectionHelper connections,
        MailFolderSyncService sync,
        RuntimeOperationSettings operationSettings,
        ISyncLockProvider locks)
        : this(db, credentials, connections, sync, operationSettings, locks, RequestDeadline)
    {
    }

    internal MailRemoteSearchService(
        AppDbContext db,
        MailCredentialResolver credentials,
        MailConnectionHelper connections,
        MailFolderSyncService sync,
        RuntimeOperationSettings operationSettings,
        ISyncLockProvider locks,
        TimeSpan deadline)
    {
        _db = db;
        _credentials = credentials;
        _connections = connections;
        _sync = sync;
        _operationSettings = operationSettings;
        _locks = locks;
        _deadline = deadline;
    }

    public async Task<RemoteSearchResponse?> SearchAsync(Guid accountId, MailSearchRequest request, CancellationToken cancellationToken)
    {
        using var activity = MailClientTelemetry.StartActivity("mailclient.mail.remote_search");

        using var deadlineCts = new CancellationTokenSource(_deadline);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
        var progress = new SearchProgress();
        try
        {
            var folders = await LoadFoldersAsync(accountId, request.FolderId, linkedCts.Token);
            if (folders is null)
                return null;
            if (request.LabelId is not null || request.ConversationId is not null)
                return new RemoteSearchResponse(0, 0, 0, true);

            var settings = (await _operationSettings.GetAsync(linkedCts.Token)).Settings.Search;
            var queryText = request.Query?.Trim();
            if (queryText is { Length: var length } && length > settings.MaxQueryLength)
                queryText = queryText[..settings.MaxQueryLength];
            var query = BuildQuery(request, queryText);
            var credential = await _credentials.ResolveAsync(accountId, linkedCts.Token);
            var endpoint = new MailServerEndpoint(credential.Account.ImapHost, credential.Account.ImapPort, credential.Account.ImapSecurity);
            await _connections.WithImapAsync(endpoint, credential.Username, credential.Secret, "RemoteSearch",
                async (client, ct) =>
                {
                    await SearchAndImportCoreAsync(accountId, folders, query, progress,
                        async (fullName, token) =>
                        {
                            var remote = new MailKitRemoteMailFolder(await client.GetFolderAsync(fullName, token));
                            await remote.OpenAsync(token);
                            return remote;
                        }, linkedCts.Token);
                    return true;
                }, linkedCts.Token, credential.AuthenticationMethod);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadlineCts.IsCancellationRequested)
        {
            progress.Complete = false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MailClientTelemetry.MarkFailed(activity, ex);
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var remaining = progress.Matched - progress.Imported;
        return new RemoteSearchResponse(progress.Matched, progress.Imported, remaining,
            progress.Complete && remaining == 0);
    }

    internal async Task SearchAndImportCoreAsync(
        Guid accountId,
        IReadOnlyList<MailFolder> folders,
        SearchQuery query,
        MailRemoteSearchService.SearchProgress progress,
        Func<string, CancellationToken, Task<IRemoteMailFolder>> openFolder,
        CancellationToken cancellationToken)
    {
        var matches = new List<FolderMatches>(folders.Count);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remote = await openFolder(folder.FullName, cancellationToken);
            var results = await remote.SearchAsync(query, cancellationToken);
            var uids = results.OrderByDescending(uid => uid.Id).ToList();
            var committed = (await _db.Mails.AsNoTracking()
                .Where(mail => mail.MailFolderId == folder.Id && mail.UidValidity == remote.UidValidity)
                .Select(mail => mail.Uid)
                .ToListAsync(cancellationToken)).ToHashSet();
            var currentValidity = await _db.SyncStates.AsNoTracking()
                .Where(state => state.MailFolderId == folder.Id)
                .Select(state => (uint?)state.UidValidity)
                .SingleOrDefaultAsync(cancellationToken);
            var skipped = currentValidity == remote.UidValidity
                ? (await _db.SyncSkippedUids.AsNoTracking()
                    .Where(skip => skip.MailFolderId == folder.Id)
                    .Select(skip => skip.Uid)
                    .ToListAsync(cancellationToken)).ToHashSet()
                : [];
            var pending = uids.Where(uid => !committed.Contains(uid.Id) && !skipped.Contains(uid.Id)).ToList();
            progress.Matched += pending.Count;
            matches.Add(new FolderMatches(folder, remote, pending));
        }
        progress.Complete = true;

        var importPlan = matches.SelectMany(item => item.Uids.Select(uid => (item.Folder, item.Remote, Uid: uid))).Take(ImportBudget).ToList();
        if (importPlan.Count == 0)
            return;

        try
        {
            await using var accountLock = await AcquireLockAsync(accountId, cancellationToken);
            if (accountLock is null)
            {
                progress.Complete = false;
                return;
            }

            foreach (var group in importPlan.GroupBy(item => item.Folder.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = group.First();
                await first.Remote.OpenAsync(cancellationToken);
                var count = await _sync.ImportRemoteMatchesAsync(accountId, first.Folder.Id, first.Remote,
                    group.Select(item => item.Uid).ToList(), cancellationToken);
                progress.Imported += count;
            }
            if (progress.Matched > ImportBudget)
                progress.Complete = false;
        }
        catch (OperationCanceledException)
        {
            progress.Complete = false;
        }
    }

    private async Task<SyncLockAcquisition?> AcquireLockAsync(Guid accountId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquisition = await _locks.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, cancellationToken);
            if (acquisition.IsAcquired)
                return acquisition;
            if (acquisition.Status == SyncLockStatus.InfrastructureFailure)
                return null;
            await Task.Delay(LockRetryDelay, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<MailFolder>?> LoadFoldersAsync(Guid accountId, Guid? folderId, CancellationToken cancellationToken)
    {
        if (folderId is { } id)
        {
            var folder = await _db.MailFolders.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id && item.MailAccountId == accountId && item.IsAvailable, cancellationToken);
            return folder is null ? null : [folder];
        }

        var folders = await _db.MailFolders.AsNoTracking()
            .Where(item => item.MailAccountId == accountId && item.IsAvailable)
            .ToListAsync(cancellationToken);
        return folders.OrderBy(item => FolderOrder(item.FolderType)).ThenBy(item => item.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static SearchQuery BuildQuery(MailSearchRequest request, string? queryText)
    {
        SearchQuery? query = null;
        void Add(SearchQuery term) => query = query is null ? term : query.And(term);
        if (!string.IsNullOrWhiteSpace(queryText)) Add(SearchQuery.MessageContains(queryText));
        if (!string.IsNullOrWhiteSpace(request.From)) Add(SearchQuery.FromContains(request.From.Trim()));
        if (!string.IsNullOrWhiteSpace(request.To))
        {
            var to = request.To.Trim();
            Add(SearchQuery.ToContains(to).Or(SearchQuery.CcContains(to)).Or(SearchQuery.BccContains(to)));
        }
        if (request.FromDate is { } fromDate) Add(SearchQuery.DeliveredAfter(fromDate.Date));
        if (request.ToDate is { } toDate) Add(SearchQuery.DeliveredBefore(toDate.Date.AddDays(1)));
        if (request.IsRead is { } isRead) Add(isRead ? SearchQuery.Seen : SearchQuery.NotSeen);
        if (request.Flagged is { } flagged) Add(flagged ? SearchQuery.Flagged : SearchQuery.NotFlagged);
        return query ?? SearchQuery.All;
    }

    private static int FolderOrder(MailFolderType type) => type switch
    {
        MailFolderType.Inbox => 0,
        MailFolderType.Sent => 1,
        MailFolderType.Archive => 2,
        MailFolderType.Custom or MailFolderType.Unknown => 3,
        MailFolderType.Drafts => 4,
        MailFolderType.Junk => 5,
        MailFolderType.Trash => 6,
        _ => 3
    };

    internal sealed class SearchProgress
    {
        public int Matched { get; set; }
        public int Imported { get; set; }
        public bool Complete { get; set; }
    }

    private sealed record FolderMatches(MailFolder Folder, IRemoteMailFolder Remote, IReadOnlyList<UniqueId> Uids);
}
