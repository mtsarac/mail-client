using MailKit;
using MailKit.Search;
using MimeKit;

namespace MailClient.Infrastructure.Email;

// Smallest useful seam around MailKit so folder sync can be tested without a live IMAP server.
// Production path wraps IMailFolder; tests supply an in-memory fake.
public sealed record RemoteSummary(uint Size, bool IsSeen);

public interface IRemoteMailFolder
{
    uint UidValidity { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    // Read-write open for flag mutation (PATCH read/unread). Sync uses OpenAsync (read-only).
    Task OpenForUpdateAsync(CancellationToken cancellationToken);

    Task<IList<UniqueId>> SearchNewAsync(uint afterUid, CancellationToken cancellationToken);

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

    public async Task<IList<UniqueId>> SearchNewAsync(uint afterUid, CancellationToken cancellationToken)
    {
        if (afterUid == uint.MaxValue)
            return [];
        return await folder.SearchAsync(
            SearchQuery.Uids(new UniqueIdRange(new UniqueId(afterUid + 1), new UniqueId(uint.MaxValue))),
            cancellationToken);
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
