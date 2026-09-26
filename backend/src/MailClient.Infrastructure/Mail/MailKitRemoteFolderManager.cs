using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Security;
using MailKit;
using MailKit.Net.Imap;

namespace MailClient.Infrastructure.Mail;

public enum RemoteFolderOutcome
{
    Succeeded,
    AlreadyExists,
    NotFound,
    HasChildren,
    NotEmpty,
    InvalidName,
    Rejected
}

public sealed record RemoteFolderResult(RemoteFolderOutcome Outcome, DiscoveredMailFolder? Folder = null);

public interface IRemoteFolderManager
{
    Task<RemoteFolderResult> CreateAsync(MailAccount account, string? parentFullName, string name, CancellationToken cancellationToken);
    Task<RemoteFolderResult> RenameAsync(MailAccount account, string fullName, string name, CancellationToken cancellationToken);
    Task<RemoteFolderResult> DeleteAsync(MailAccount account, string fullName, CancellationToken cancellationToken);
}

public sealed class MailKitRemoteFolderManager(
    MailCredentialResolver credentials,
    MailConnectionHelper connections) : IRemoteFolderManager
{
    public Task<RemoteFolderResult> CreateAsync(MailAccount account, string? parentFullName, string name, CancellationToken cancellationToken) =>
        RunAsync(account, "CreateFolder", (imap, ct) => CreateAsync(imap, parentFullName, name, ct), cancellationToken);

    public Task<RemoteFolderResult> RenameAsync(MailAccount account, string fullName, string name, CancellationToken cancellationToken) =>
        RunAsync(account, "RenameFolder", (imap, ct) => RenameAsync(imap, fullName, name, ct), cancellationToken);

    public Task<RemoteFolderResult> DeleteAsync(MailAccount account, string fullName, CancellationToken cancellationToken) =>
        RunAsync(account, "DeleteFolder", (imap, ct) => DeleteAsync(imap, fullName, ct), cancellationToken);

    internal static async Task<RemoteFolderResult> CreateAsync(ImapClient imap, string? parentFullName, string name, CancellationToken cancellationToken)
    {
        IMailFolder parent;
        if (parentFullName is null)
        {
            if (imap.PersonalNamespaces.Count == 0)
                return new(RemoteFolderOutcome.Rejected);
            parent = imap.GetFolder(imap.PersonalNamespaces[0]);
        }
        else if (await FindAsync(imap, parentFullName, cancellationToken) is { } found)
        {
            parent = found;
        }
        else
        {
            return new(RemoteFolderOutcome.NotFound);
        }

        if (ContainsSeparator(parent, name))
            return new(RemoteFolderOutcome.InvalidName);
        if (await SiblingExistsAsync(parent, name, null, cancellationToken))
            return new(RemoteFolderOutcome.AlreadyExists);

        IMailFolder? created;
        try
        {
            created = await parent.CreateAsync(name, true, cancellationToken);
        }
        catch (ImapCommandException)
        {
            return new(RemoteFolderOutcome.Rejected);
        }

        created ??= (await parent.GetSubfoldersAsync(false, cancellationToken))
            .FirstOrDefault(folder => string.Equals(folder.Name, name, StringComparison.Ordinal));
        return created is null
            ? new(RemoteFolderOutcome.Rejected)
            : new(RemoteFolderOutcome.Succeeded, DiscoveredMailFolder.From(created, await ReadUidValidityAsync(created, cancellationToken)));
    }

    internal static async Task<RemoteFolderResult> RenameAsync(ImapClient imap, string fullName, string name, CancellationToken cancellationToken)
    {
        if (await FindAsync(imap, fullName, cancellationToken) is not { } folder)
            return new(RemoteFolderOutcome.NotFound);
        if (folder.ParentFolder is not { } parent)
            return new(RemoteFolderOutcome.Rejected);
        if (ContainsSeparator(folder, name))
            return new(RemoteFolderOutcome.InvalidName);
        if (await SiblingExistsAsync(parent, name, folder.FullName, cancellationToken))
            return new(RemoteFolderOutcome.AlreadyExists);

        try
        {
            await folder.RenameAsync(parent, name, cancellationToken);
        }
        catch (ImapCommandException)
        {
            return new(RemoteFolderOutcome.Rejected);
        }

        return new(RemoteFolderOutcome.Succeeded, DiscoveredMailFolder.From(folder, folder.UidValidity));
    }

    internal static async Task<RemoteFolderResult> DeleteAsync(ImapClient imap, string fullName, CancellationToken cancellationToken)
    {
        if (await FindAsync(imap, fullName, cancellationToken) is not { } folder)
            return new(RemoteFolderOutcome.NotFound);
        if ((await folder.GetSubfoldersAsync(false, cancellationToken)).Count > 0)
            return new(RemoteFolderOutcome.HasChildren);
        if (!folder.Attributes.HasFlag(FolderAttributes.NoSelect))
        {
            await folder.StatusAsync(StatusItems.Count, cancellationToken);
            if (folder.Count > 0)
                return new(RemoteFolderOutcome.NotEmpty);
        }

        try
        {
            await folder.DeleteAsync(cancellationToken);
        }
        catch (ImapCommandException)
        {
            return new(RemoteFolderOutcome.Rejected);
        }

        return new(RemoteFolderOutcome.Succeeded);
    }

    private async Task<RemoteFolderResult> RunAsync(
        MailAccount account,
        string operation,
        Func<ImapClient, CancellationToken, Task<RemoteFolderResult>> action,
        CancellationToken cancellationToken)
    {
        var resolved = await credentials.ResolveAsync(account.Id, cancellationToken);
        var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        return await connections.WithImapAsync(endpoint, resolved.Username, resolved.Secret, operation, action,
            cancellationToken, resolved.AuthenticationMethod);
    }

    private static async Task<IMailFolder?> FindAsync(ImapClient imap, string fullName, CancellationToken cancellationToken)
    {
        try
        {
            return await imap.GetFolderAsync(fullName, cancellationToken);
        }
        catch (FolderNotFoundException)
        {
            return null;
        }
    }

    private static bool ContainsSeparator(IMailFolder folder, string name) =>
        folder.DirectorySeparator != '\0' && name.Contains(folder.DirectorySeparator);

    private static async Task<bool> SiblingExistsAsync(IMailFolder parent, string name, string? exceptFullName, CancellationToken cancellationToken) =>
        (await parent.GetSubfoldersAsync(false, cancellationToken)).Any(sibling =>
            string.Equals(sibling.Name, name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sibling.FullName, exceptFullName, StringComparison.Ordinal));

    private static async Task<uint> ReadUidValidityAsync(IMailFolder folder, CancellationToken cancellationToken)
    {
        if (folder.Attributes.HasFlag(FolderAttributes.NoSelect))
            return 0;
        try
        {
            await folder.StatusAsync(StatusItems.UidValidity, cancellationToken);
        }
        catch (ImapCommandException)
        {
        }

        return folder.UidValidity;
    }
}
