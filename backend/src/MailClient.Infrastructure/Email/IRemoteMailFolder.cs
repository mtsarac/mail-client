using MailKit;
using MailKit.Search;
using MimeKit;

namespace MailClient.Infrastructure.Email;

// Smallest useful seam around MailKit so folder sync can be tested without a live IMAP server.
// Production path wraps IMailFolder; tests supply an in-memory fake.
internal interface IRemoteMailFolder
{
    uint UidValidity { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    Task<IList<UniqueId>> SearchNewAsync(uint afterUid, CancellationToken cancellationToken);

    // Batch SIZE lookup for the current bounded run. Returns only summaries the
    // server actually returned; a UID missing from the dictionary (or mapped to
    // null) means the message is gone and must be skipped without downloading.
    Task<IReadOnlyDictionary<uint, uint?>> GetSizesAsync(
        IReadOnlyCollection<UniqueId> uids,
        CancellationToken cancellationToken);

    Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken);
}

internal sealed class MailKitRemoteMailFolder(IMailFolder folder) : IRemoteMailFolder
{
    public uint UidValidity => folder.UidValidity;

    public Task OpenAsync(CancellationToken cancellationToken) =>
        folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

    public async Task<IList<UniqueId>> SearchNewAsync(uint afterUid, CancellationToken cancellationToken)
    {
        if (afterUid == uint.MaxValue)
            return [];
        return await folder.SearchAsync(
            SearchQuery.Uids(new UniqueIdRange(new UniqueId(afterUid + 1), new UniqueId(uint.MaxValue))),
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<uint, uint?>> GetSizesAsync(
        IReadOnlyCollection<UniqueId> uids,
        CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
            return new Dictionary<uint, uint?>();
        var summaries = await folder.FetchAsync([.. uids], MessageSummaryItems.Size, cancellationToken);
        return summaries.ToDictionary(summary => summary.UniqueId.Id, summary => summary.Size);
    }

    public Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken) =>
        folder.GetMessageAsync(uid, cancellationToken);
}
