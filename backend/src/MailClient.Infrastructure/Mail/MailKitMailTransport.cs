using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Security;
using MimeKit;

namespace MailClient.Infrastructure.Mail;

public interface IMailTransport
{
    Task SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken, bool requestDeliveryReceipt = false);
    Task AppendToSentAsync(MailAccount account, string sentFullName, MimeMessage message, CancellationToken cancellationToken);
}

public sealed class DeliveryReceiptNotSupportedException : Exception;

public sealed class MailKitMailTransport(
    MailCredentialResolver credentials,
    MailConnectionHelper connections) : IMailTransport
{
    public async Task SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken, bool requestDeliveryReceipt = false)
    {
        var resolved = await credentials.ResolveAsync(account.Id, cancellationToken);
        var endpoint = new MailServerEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity);
        await connections.WithSmtpAsync(
            endpoint,
            resolved.Username,
            resolved.Secret,
            "SendMail",
            async (client, ct) =>
            {
                if (requestDeliveryReceipt)
                {
                    if (!client.Capabilities.HasFlag(MailKit.Net.Smtp.SmtpCapabilities.Dsn) || client is not ReceiptSmtpClient receiptClient)
                        throw new DeliveryReceiptNotSupportedException();
                    receiptClient.RequestDeliveryReceipt = true;
                }

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
