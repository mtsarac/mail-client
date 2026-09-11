using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Domain.Entities;

// Scoped IMAP folder access with advisory locking and flag operations.
namespace MailClient.Infrastructure.Email;

public interface IMailFolderClient
{
    Task<T> UseFolderAsync<T>(
        MailAccount account,
        string fullName,
        bool forUpdate,
        Func<IRemoteMailFolder, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);
}

public sealed class MailFolderClient(
    ICredentialProtector credentials,
    MailConnectionHelper connections) : IMailFolderClient
{
    public async Task<T> UseFolderAsync<T>(
        MailAccount account,
        string fullName,
        bool forUpdate,
        Func<IRemoteMailFolder, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var password = credentials.Unprotect(account.EncryptedPassword);
        var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        return await connections.WithImapAsync(
            endpoint,
            account.Username,
            password,
            forUpdate ? "UpdateFlags" : "ReadFlags",
            async (client, ct) =>
            {
                var remote = new MailKitRemoteMailFolder(await client.GetFolderAsync(fullName, ct));
                if (forUpdate)
                    await remote.OpenForUpdateAsync(ct);
                else
                    await remote.OpenAsync(ct);
                return await action(remote, ct);
            },
            cancellationToken);
    }
}
