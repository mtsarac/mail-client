using MailKit;
using MailKit.Search;
using MimeKit;

namespace MailClient.Infrastructure.Email;

public sealed record RemoteSummary(
    uint Size,
    bool IsSeen,
    bool IsAnswered,
    bool IsFlagged,
    bool IsDraft,
    bool IsDeleted,
    bool IsRecent,
    DateTime? InternalDate);

public sealed record UidSearchResult(IList<UniqueId> Uids, uint ScannedUpTo);

/// <summary>Newest-first backfill page. <paramref name="NextHighExclusive"/> is the upper bound (exclusive)
/// for the next older page; 0 means the folder has been walked back to UID 1.</summary>
public sealed record UidBackfillResult(IList<UniqueId> Uids, long NextHighExclusive);

/// <summary>Server-side flag state used to converge local rows with the mailbox.</summary>
public sealed record RemoteMessageFlags(bool Seen, bool Answered, bool Flagged, bool Draft, bool Deleted);
public sealed record RemoteMoveResult(UniqueId? DestinationUid, uint DestinationUidValidity);
public sealed record RemoteAppendResult(UniqueId? DestinationUid, uint DestinationUidValidity);

public interface IRemoteMailFolder
{
    uint UidValidity { get; }

    /// <summary>Server UIDNEXT, or 0 when the server did not report it.</summary>
    uint UidNext { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task OpenForUpdateAsync(CancellationToken cancellationToken);
    Task<UidSearchResult> SearchNewAsync(uint afterUid, int maxCount, CancellationToken cancellationToken);
    Task<UidBackfillResult> SearchOlderAsync(long belowUidExclusive, int maxCount, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<uint, RemoteSummary?>> GetSummariesAsync(IReadOnlyCollection<UniqueId> uids, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<uint, RemoteMessageFlags>> GetFlagsAsync(IReadOnlyCollection<UniqueId> uids, CancellationToken cancellationToken);
    Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken);
    Task SetSeenAsync(UniqueId uid, bool seen, CancellationToken cancellationToken);
    Task SetFlaggedAsync(UniqueId uid, bool flagged, CancellationToken cancellationToken);
    Task<RemoteMoveResult> MoveAsync(UniqueId uid, string destinationFullName, CancellationToken cancellationToken);
    Task<RemoteMoveResult> CopyAsync(UniqueId uid, string destinationFullName, CancellationToken cancellationToken);
    Task<RemoteAppendResult> AppendAsync(MimeMessage message, MessageFlags flags, CancellationToken cancellationToken);
}

public sealed class MailKitRemoteMailFolder(IMailFolder folder, Func<string, CancellationToken, Task<IMailFolder>>? resolveFolder = null) : IRemoteMailFolder
{
    public uint UidValidity => folder.UidValidity;

    public uint UidNext => folder.UidNext?.Id ?? 0;

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

    public Task<UidBackfillResult> SearchOlderAsync(long belowUidExclusive, int maxCount, CancellationToken cancellationToken) =>
        SearchOlderPagedAsync(
            belowUidExclusive,
            maxCount,
            (low, high, ct) => folder.SearchAsync(
                SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)low), new UniqueId((uint)high))), ct),
            cancellationToken);

    /// <summary>
    /// Walks the folder downwards from <paramref name="belowUidExclusive"/> so the newest history is imported
    /// first. The returned bound resumes strictly below the oldest UID this page covered, so nothing is skipped.
    /// </summary>
    internal static async Task<UidBackfillResult> SearchOlderPagedAsync(
        long belowUidExclusive,
        int maxCount,
        Func<ulong, ulong, CancellationToken, Task<IList<UniqueId>>> searchPage,
        CancellationToken cancellationToken)
    {
        const int MaxPages = 8;
        const ulong GrowthFactor = 4;
        if (belowUidExclusive <= 1 || maxCount <= 0)
            return new UidBackfillResult([], 0);
        var found = new List<UniqueId>();
        var high = (ulong)Math.Min(belowUidExclusive - 1, uint.MaxValue);
        var window = (ulong)Math.Max(4L * maxCount, 1);
        var pages = 0;
        long nextHighExclusive = 0;
        while (found.Count < maxCount && pages < MaxPages && high >= 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var low = high >= window ? high - window + 1 : 1;
            var page = (await searchPage(low, high, cancellationToken)).OrderByDescending(uid => uid.Id).ToList();
            pages++;
            nextHighExclusive = (long)low;
            foreach (var uid in page)
            {
                if (found.Count >= maxCount)
                {
                    // Page truncated: resume immediately below the oldest UID actually taken.
                    nextHighExclusive = found[^1].Id;
                    break;
                }

                found.Add(uid);
            }

            if (found.Count >= maxCount || low <= 1)
                break;
            high = low - 1;
            window = Math.Min(page.Count == 0 ? window * GrowthFactor : window * 2, (ulong)uint.MaxValue);
        }

        found.Sort((left, right) => left.Id.CompareTo(right.Id));
        return new UidBackfillResult(found, nextHighExclusive <= 1 ? 0 : nextHighExclusive);
    }

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
            [.. uids],
            MessageSummaryItems.UniqueId
                | MessageSummaryItems.Size
                | MessageSummaryItems.Flags
                | MessageSummaryItems.InternalDate,
            cancellationToken);
        return summaries.ToDictionary(
            summary => summary.UniqueId.Id,
            summary => summary.Size is uint size
                ? (RemoteSummary?)new RemoteSummary(
                    size,
                    summary.Flags?.HasFlag(MessageFlags.Seen) == true,
                    summary.Flags?.HasFlag(MessageFlags.Answered) == true,
                    summary.Flags?.HasFlag(MessageFlags.Flagged) == true,
                    summary.Flags?.HasFlag(MessageFlags.Draft) == true,
                    summary.Flags?.HasFlag(MessageFlags.Deleted) == true,
                    summary.Flags?.HasFlag(MessageFlags.Recent) == true,
                    summary.InternalDate?.UtcDateTime)
                : null);
    }

    public async Task<IReadOnlyDictionary<uint, RemoteMessageFlags>> GetFlagsAsync(
        IReadOnlyCollection<UniqueId> uids,
        CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
            return new Dictionary<uint, RemoteMessageFlags>();
        var summaries = await folder.FetchAsync(
            [.. uids], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags,
            cancellationToken);
        return summaries.ToDictionary(
            summary => summary.UniqueId.Id,
            summary => new RemoteMessageFlags(
                summary.Flags?.HasFlag(MessageFlags.Seen) == true,
                summary.Flags?.HasFlag(MessageFlags.Answered) == true,
                summary.Flags?.HasFlag(MessageFlags.Flagged) == true,
                summary.Flags?.HasFlag(MessageFlags.Draft) == true,
                summary.Flags?.HasFlag(MessageFlags.Deleted) == true));
    }

    public Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken) =>
        folder.GetMessageAsync(uid, cancellationToken);

    public Task SetSeenAsync(UniqueId uid, bool seen, CancellationToken cancellationToken) =>
        seen
            ? folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken)
            : folder.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);

    public Task SetFlaggedAsync(UniqueId uid, bool flagged, CancellationToken cancellationToken) =>
        flagged
            ? folder.AddFlagsAsync(uid, MessageFlags.Flagged, true, cancellationToken)
            : folder.RemoveFlagsAsync(uid, MessageFlags.Flagged, true, cancellationToken);

    public async Task<RemoteMoveResult> MoveAsync(UniqueId uid, string destinationFullName, CancellationToken cancellationToken)
    {
        if (resolveFolder is null)
            throw new NotSupportedException("Destination folder resolution is unavailable.");
        var destination = await resolveFolder(destinationFullName, cancellationToken);
        var result = await folder.MoveToAsync(uid, destination, cancellationToken);
        var uidValidity = await ResolveUidValidityAsync(destination, cancellationToken);
        return new(result, uidValidity);
    }

    public async Task<RemoteMoveResult> CopyAsync(UniqueId uid, string destinationFullName, CancellationToken cancellationToken)
    {
        if (resolveFolder is null)
            throw new NotSupportedException("Destination folder resolution is unavailable.");
        var destination = await resolveFolder(destinationFullName, cancellationToken);
        var result = await folder.CopyToAsync(uid, destination, cancellationToken);
        var uidValidity = await ResolveUidValidityAsync(destination, cancellationToken);
        return new(result, uidValidity);
    }

    private static async Task<uint> ResolveUidValidityAsync(IMailFolder destination, CancellationToken cancellationToken)
    {
        if (destination.UidValidity != 0)
            return destination.UidValidity;
        await destination.StatusAsync(StatusItems.UidValidity, cancellationToken);
        return destination.UidValidity;
    }

    public async Task<RemoteAppendResult> AppendAsync(
        MimeMessage message,
        MessageFlags flags,
        CancellationToken cancellationToken)
    {
        var uid = await folder.AppendAsync(message, flags, cancellationToken);
        return new(uid, folder.UidValidity);
    }
}
