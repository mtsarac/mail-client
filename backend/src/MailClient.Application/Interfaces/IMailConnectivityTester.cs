using MailClient.Application.Network;

namespace MailClient.Application.Interfaces;

public interface IMailConnectivityTester
{
    Task TestImapAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken);

    Task TestSmtpAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken);
}
