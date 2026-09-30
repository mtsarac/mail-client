using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Security;
using MimeKit;

namespace MailClient.Infrastructure.Mail;

public interface IMailTransport
{
    Task SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken);
    Task AppendToSentAsync(MailAccount account, string sentFullName, MimeMessage message, CancellationToken cancellationToken);
}

public sealed class MailKitMailTransport(
    MailCredentialResolver credentials,
    MailConnectionHelper connections) : IMailTransport
{
    public async Task SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken)
    {
        var resolved = await ResolveForSendAsync(account.Id, cancellationToken);
        var endpoint = new MailServerEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity);
        await connections.WithSmtpAsync(
            endpoint,
            resolved.Username,
            resolved.Secret,
            "SendMail",
            async (client, ct) =>
            {
                try
                {
                    await client.SendAsync(message, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new SmtpDeliveryException("SMTP delivery outcome is unknown.", ex);
                }

                return true;
            },
            cancellationToken,
            resolved.AuthenticationMethod);
    }

    private async Task<ResolvedCredential> ResolveForSendAsync(Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            return await credentials.ResolveAsync(accountId, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or System.Net.Sockets.SocketException
            || ex is InvalidOperationException { Message: SyncFailureClassifier.OAuthRefreshLockUnavailable })
        {
            // Credentials are resolved before opening SMTP, so this failure cannot mean delivery occurred.
            throw new MailConnectionException(MailConnectionFailure.Network,
                MailConnectionErrorClassifier.SafeMessage(MailConnectionFailure.Network), ex)
            { Operation = "SendMail" };
        }
    }

    public async Task AppendToSentAsync(
        MailAccount account, string sentFullName, MimeMessage message, CancellationToken cancellationToken)
    {
        var resolved = await credentials.ResolveAsync(account.Id, cancellationToken);
        var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        await connections.WithImapAsync(
            endpoint,
            resolved.Username,
            resolved.Secret,
            "AppendSent",
            async (client, ct) =>
            {
                var remote = new MailKitRemoteMailFolder(await client.GetFolderAsync(sentFullName, ct));
                await remote.OpenForUpdateAsync(ct);
                await remote.AppendAsync(message, MailKit.MessageFlags.Seen, ct);
                return true;
            },
            cancellationToken,
            resolved.AuthenticationMethod);
    }
}
