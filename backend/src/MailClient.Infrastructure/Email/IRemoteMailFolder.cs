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

    Task<uint?> GetSizeAsync(UniqueId uid, CancellationToken cancellationToken);

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

    public async Task<uint?> GetSizeAsync(UniqueId uid, CancellationToken cancellationToken)
    {
        var summaries = await folder.FetchAsync([uid], MessageSummaryItems.Size, cancellationToken);
        return summaries.Count == 0 ? null : summaries[0].Size;
    }

    public Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken) =>
        folder.GetMessageAsync(uid, cancellationToken);
}
