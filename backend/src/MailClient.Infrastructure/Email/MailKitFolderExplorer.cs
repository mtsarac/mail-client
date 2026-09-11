using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Infrastructure.Services;
using MailKit;
using MailKit.Net.Imap;

// Discovers IMAP folders and maps special-use attributes to folder types.
namespace MailClient.Infrastructure.Email;

public sealed class MailKitFolderExplorer(MailConnectionHelper connections) : IMailFolderExplorer
{
    public async Task<IReadOnlyList<DiscoveredMailFolder>> ExploreAsync(
        MailServerEndpoint endpoint,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return await connections.WithImapAsync(
            endpoint, username, password, "ExploreFolders", CollectAsync, cancellationToken);
    }

    private static async Task<IReadOnlyList<DiscoveredMailFolder>> CollectAsync(
        ImapClient imap, CancellationToken cancellationToken)
    {
        var discovered = new List<DiscoveredMailFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CollectFolderAsync(imap.Inbox, discovered, seen, cancellationToken);

        foreach (var ns in imap.PersonalNamespaces)
        {
            var folders = await imap.GetFoldersAsync(ns, false, cancellationToken);
            foreach (var folder in folders)
                await CollectFolderAsync(folder, discovered, seen, cancellationToken);
        }

        if (discovered.Count == 0)
        {
            foreach (var folder in await imap.Inbox.GetSubfoldersAsync(false, cancellationToken))
                await CollectFolderAsync(folder, discovered, seen, cancellationToken);
        }

        return discovered;
    }

    private static async Task CollectFolderAsync(
        IMailFolder folder,
        List<DiscoveredMailFolder> discovered,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        if (seen.Add(folder.FullName))
        {
            uint uidValidity = 0;
            if (!folder.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                try
                {
                    await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
                    uidValidity = folder.UidValidity;
                    await folder.CloseAsync(false, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    uidValidity = folder.UidValidity;
                }
            }

            var classification = MailFolderDiscovery.Classify(folder.Attributes, folder.FullName);
            discovered.Add(new DiscoveredMailFolder(
                folder.Name, folder.FullName, classification.FolderType, uidValidity, classification.IsSyncEnabled));
        }

        IList<IMailFolder> children;
        try
        {
            children = await folder.GetSubfoldersAsync(false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return;
        }

        foreach (var child in children)
            await CollectFolderAsync(child, discovered, seen, cancellationToken);
    }
}
