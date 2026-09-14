using MailClient.Application.Network;
using MailClient.Domain.Enums;

// IMAP folder-discovery seam with the discovered-folder model.
namespace MailClient.Application.Interfaces;

public sealed record DiscoveredMailFolder(
    string Name,
    string FullName,
    MailFolderType FolderType,
    uint UidValidity,
    bool IsSyncEnabled);

public interface IMailFolderExplorer
{
    Task<IReadOnlyList<DiscoveredMailFolder>> ExploreAsync(
        MailServerEndpoint endpoint,
        string username,
        string password,
        CancellationToken cancellationToken);
}
