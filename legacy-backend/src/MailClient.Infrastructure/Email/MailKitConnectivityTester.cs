using MailClient.Application.Interfaces;
using MailClient.Application.Network;

// Tests IMAP/SMTP reachability and credentials for account setup.
namespace MailClient.Infrastructure.Email;

public sealed class MailKitConnectivityTester(MailConnectionHelper connections) : IMailConnectivityTester
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
