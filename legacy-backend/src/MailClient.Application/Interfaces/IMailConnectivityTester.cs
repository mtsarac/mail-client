using MailClient.Application.Network;

// IMAP/SMTP connectivity-test seam used during account creation and testing.
namespace MailClient.Application.Interfaces;

public interface IMailConnectivityTester
{
    Task TestImapAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken);

    Task TestSmtpAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken);
}
