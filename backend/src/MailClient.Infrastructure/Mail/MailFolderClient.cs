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

public sealed class MailKitConnectivityTester(MailConnectionHelper connections)
{
    public async Task TestImapAsync(
        MailServerEndpoint endpoint,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        await connections.WithImapAsync(
            endpoint, username, password, "TestImap",
            static async (client, ct) => { await client.NoOpAsync(ct); return true; },
            cancellationToken);
    }

    public async Task TestSmtpAsync(
        MailServerEndpoint endpoint,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        await connections.WithSmtpAsync(
            endpoint, username, password, "TestSmtp",
            static async (client, ct) => { await client.NoOpAsync(ct); return true; },
            cancellationToken);
    }
}
