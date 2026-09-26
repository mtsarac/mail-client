using System.Net.Sockets;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Network;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Mail;

public class MailConnectionHelper(
    OutboundHostValidator hosts,
    ILogger<MailConnectionHelper> logger,
    MailClientMetrics? metrics = null)
{
    private const int OperationTimeoutMs = 30_000;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);

    public async Task ProbeImapAsync(MailServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);
        using var client = new ImapClient { Timeout = OperationTimeoutMs };
        await RunAsync(client, endpoint, null, null, "DiscoverImap", static (_, _) => Task.FromResult(true), timeout.Token);
    }

    public async Task ProbeSmtpAsync(MailServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);
        using var client = new SmtpClient { Timeout = OperationTimeoutMs };
        await RunAsync(client, endpoint, null, null, "DiscoverSmtp", static async (service, ct) =>
        {
            await ((SmtpClient)service).NoOpAsync(ct);
            return true;
        }, timeout.Token);
    }

    public virtual async Task<T> WithImapAsync<T>(
        MailServerEndpoint endpoint,
        string username,
        string password,
        string operation,
        Func<ImapClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        AuthenticationMethod authenticationMethod = AuthenticationMethod.Password)
    {
        using var client = new ImapClient { Timeout = OperationTimeoutMs };
        return await RunAsync(client, endpoint, username, password, operation,
            (service, ct) => action((ImapClient)service, ct), cancellationToken, authenticationMethod);
    }

    public virtual async Task<T> WithSmtpAsync<T>(
        MailServerEndpoint endpoint,
        string username,
        string password,
        string operation,
        Func<SmtpClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        AuthenticationMethod authenticationMethod = AuthenticationMethod.Password)
    {
        using var client = new ReceiptSmtpClient { Timeout = OperationTimeoutMs };
        return await RunAsync(client, endpoint, username, password, operation,
            (service, ct) => action((SmtpClient)service, ct), cancellationToken, authenticationMethod);
    }

    private async Task<T> RunAsync<T>(
        MailService client,
        MailServerEndpoint endpoint,
        string? username,
        string? password,
        string operation,
        Func<MailService, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        AuthenticationMethod authenticationMethod = AuthenticationMethod.Password)
    {
        try
        {
            var destination = await hosts.ResolveAllowedAsync(endpoint.Host, cancellationToken);
            using var socket = new Socket(destination.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(destination.Address, endpoint.Port, cancellationToken);
            await client.ConnectAsync(socket, endpoint.Host, endpoint.Port,
                ToSocketOptions(endpoint.Security), cancellationToken);
            if (username is not null && password is not null)
            {
                if (authenticationMethod == AuthenticationMethod.OAuth2)
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(username, password), cancellationToken);
                else
                    await client.AuthenticateAsync(username, password, cancellationToken);
            }
            return await action(client, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogInformation(ex, "Mail operation {Operation} cancelled for host {Host}.", operation, LogSafe(endpoint.Host));
            throw;
        }
        catch (Exception ex) when (ex is MailConnectionException or SmtpDeliveryException or DeliveryReceiptNotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = MailConnectionErrorClassifier.Classify(ex);
            metrics?.RecordMailConnectionFailure(client is SmtpClient ? "smtp" : "imap", failure);
            logger.LogError(ex,
                "Mail operation {Operation} failed ({Failure}) for host {Host} on port {Port}.",
                operation, failure, LogSafe(endpoint.Host), endpoint.Port);
            throw new MailConnectionException(failure, MailConnectionErrorClassifier.SafeMessage(failure), ex) { Operation = operation };
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Mail disconnect failed for host {Host}.", LogSafe(endpoint.Host));
                }
            }
        }
    }

    private static string LogSafe(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private static SecureSocketOptions ToSocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => throw new ArgumentOutOfRangeException(nameof(security))
    };
}

public sealed class ReceiptSmtpClient : SmtpClient
{
    public bool RequestDeliveryReceipt { get; set; }

    protected override DeliveryStatusNotification? GetDeliveryStatusNotifications(MimeKit.MimeMessage message, MimeKit.MailboxAddress mailbox) =>
        RequestDeliveryReceipt ? DeliveryStatusNotification.Success | DeliveryStatusNotification.Failure : null;
}
