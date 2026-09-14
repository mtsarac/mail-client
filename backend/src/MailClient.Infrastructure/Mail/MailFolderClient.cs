using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Security;

namespace MailClient.Infrastructure.Mail;

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
    MailCredentialResolver credentials,
    MailConnectionHelper connections) : IMailFolderClient
{
    public async Task<T> UseFolderAsync<T>(
        MailAccount account,
        string fullName,
        bool forUpdate,
        Func<IRemoteMailFolder, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var resolved = await credentials.ResolveAsync(account.Id, cancellationToken);
        var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        return await connections.WithImapAsync(
            endpoint,
            resolved.Username,
            resolved.Password,
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
