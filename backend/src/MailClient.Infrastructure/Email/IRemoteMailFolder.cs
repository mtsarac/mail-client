using MailKit;
using MailKit.Search;
using MimeKit;

// Testable IMAP folder abstraction (summaries, UID search, flag updates) with a MailKit adapter.
namespace MailClient.Infrastructure.Email;

// Smallest useful seam around MailKit so folder sync can be tested without a live IMAP server.
// Production path wraps IMailFolder; tests supply an in-memory fake.
public sealed record RemoteSummary(uint Size, bool IsSeen);

public sealed record UidSearchResult(IList<UniqueId> Uids, uint ScannedUpTo);

public interface IRemoteMailFolder
{
    uint UidValidity { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    // Read-write open for flag mutation (PATCH read/unread). Sync uses OpenAsync (read-only).
    Task OpenForUpdateAsync(CancellationToken cancellationToken);

    // New UIDs from the scan cursor, ascending, at most maxCount. Both the
    // materialized UID count and the SEARCH roundtrips per call are bounded
    // (adaptive UID windows, capped page count); UidNext bounds the scan.
    Task<UidSearchResult> SearchNewAsync(uint afterUid, int maxCount, CancellationToken cancellationToken);

    // Batch UID + SIZE + FLAGS lookup for the current bounded run. Returns only
    // summaries the server actually returned; a UID missing from the dictionary
    // (or mapped to null) means the message is gone and must be skipped without
    // downloading.
    Task<IReadOnlyDictionary<uint, RemoteSummary?>> GetSummariesAsync(
        IReadOnlyCollection<UniqueId> uids,
        CancellationToken cancellationToken);

    // FLAGS-only batched lookup for flag reconciliation. Never fetches bodies.
    Task<IReadOnlyDictionary<uint, bool>> GetFlagsAsync(
        IReadOnlyCollection<UniqueId> uids,
        CancellationToken cancellationToken);

    Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken);

    Task SetSeenAsync(UniqueId uid, bool seen, CancellationToken cancellationToken);

    Task AppendAsync(MimeMessage message, CancellationToken cancellationToken);
}

public sealed class MailKitRemoteMailFolder(IMailFolder folder) : IRemoteMailFolder
{
    public uint UidValidity => folder.UidValidity;

    public Task OpenAsync(CancellationToken cancellationToken) =>
        folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

    public Task OpenForUpdateAsync(CancellationToken cancellationToken) =>
        folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

    public Task<UidSearchResult> SearchNewAsync(
        uint afterUid, int maxCount, CancellationToken cancellationToken) =>
        SearchPagedAsync(
            afterUid,
            maxCount,
            () => folder.UidNext?.Id ?? 0,
            (low, high, ct) => folder.SearchAsync(
                SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)low), new UniqueId((uint)high))), ct),
            cancellationToken);

    internal static async Task<UidSearchResult> SearchPagedAsync(
        uint afterUid,
        int maxCount,
        Func<uint> getUidNext,
        Func<ulong, ulong, CancellationToken, Task<IList<UniqueId>>> searchPage,
        CancellationToken cancellationToken)
    {
        const int MaxPages = 8;
        const ulong GrowthFactor = 4;
        if (afterUid == uint.MaxValue || maxCount <= 0)
            return new UidSearchResult([], afterUid);
        var found = new List<UniqueId>();
        var low = (ulong)afterUid + 1;
        var window = (ulong)Math.Max(4L * maxCount, 1);
        var pages = 0;
        var scannedUpTo = (ulong)afterUid;
        while (found.Count < maxCount && pages < MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uidNext = getUidNext();
            if (uidNext == 0 || low >= uidNext)
                break;
            var high = Math.Min(low + window - 1, (ulong)uidNext - 1);
            var page = (await searchPage(low, high, cancellationToken)).OrderBy(uid => uid.Id).ToList();
            pages++;
            scannedUpTo = high;
            foreach (var uid in page)
            {
                if (found.Count >= maxCount)
                    break;
                found.Add(uid);
            }

            if (high >= (ulong)uidNext - 1)
                break;
            low = high + 1;
            window = Math.Min(page.Count == 0 ? window * GrowthFactor : window * 2, (ulong)uint.MaxValue);
        }

        found.Sort((left, right) => left.Id.CompareTo(right.Id));
        return new UidSearchResult(found, (uint)scannedUpTo);
    }

    public async Task<IReadOnlyDictionary<uint, RemoteSummary?>> GetSummariesAsync(
        IReadOnlyCollection<UniqueId> uids,
        CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
            return new Dictionary<uint, RemoteSummary?>();
        var summaries = await folder.FetchAsync(
            [.. uids], MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Flags,
            cancellationToken);
        return summaries.ToDictionary(
            summary => summary.UniqueId.Id,
            summary => summary.Size is uint size
                ? (RemoteSummary?)new RemoteSummary(
                    size,
                    summary.Flags?.HasFlag(MessageFlags.Seen) == true)
                : null);
    }

    public async Task<IReadOnlyDictionary<uint, bool>> GetFlagsAsync(
        IReadOnlyCollection<UniqueId> uids,
        CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
            return new Dictionary<uint, bool>();
        var summaries = await folder.FetchAsync(
            [.. uids], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags,
            cancellationToken);
        return summaries.ToDictionary(
            summary => summary.UniqueId.Id,
            summary => summary.Flags?.HasFlag(MessageFlags.Seen) == true);
    }

    public Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken) =>
        folder.GetMessageAsync(uid, cancellationToken);

    public Task SetSeenAsync(UniqueId uid, bool seen, CancellationToken cancellationToken) =>
        seen
            ? folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken)
            : folder.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);

    public Task AppendAsync(MimeMessage message, CancellationToken cancellationToken) =>
        folder.AppendAsync(message, MessageFlags.Seen, cancellationToken);
}
