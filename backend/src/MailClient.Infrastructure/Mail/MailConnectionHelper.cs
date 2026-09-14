using System.Net.Sockets;
using MailClient.Application.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Network;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Mail;

public sealed class MailConnectionHelper(
    OutboundHostValidator hosts,
    ILogger<MailConnectionHelper> logger)
{
    private const int OperationTimeoutMs = 30_000;

    public async Task<T> WithImapAsync<T>(
        MailServerEndpoint endpoint,
        string username,
        string password,
        string operation,
        Func<ImapClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var client = new ImapClient { Timeout = OperationTimeoutMs };
        return await RunAsync(client, endpoint, username, password, operation,
            (service, ct) => action((ImapClient)service, ct), cancellationToken);
    }

    public async Task<T> WithSmtpAsync<T>(
        MailServerEndpoint endpoint,
        string username,
        string password,
        string operation,
        Func<SmtpClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient { Timeout = OperationTimeoutMs };
        return await RunAsync(client, endpoint, username, password, operation,
            (service, ct) => action((SmtpClient)service, ct), cancellationToken);
    }

    private async Task<T> RunAsync<T>(
        MailService client,
        MailServerEndpoint endpoint,
        string username,
        string password,
        string operation,
        Func<MailService, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var destination = await hosts.ResolveAllowedAsync(endpoint.Host, cancellationToken);
            using var socket = new Socket(destination.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(destination.Address, endpoint.Port, cancellationToken);
            await client.ConnectAsync(socket, endpoint.Host, endpoint.Port,
                ToSocketOptions(endpoint.Security), cancellationToken);
            await client.AuthenticateAsync(username, password, cancellationToken);
            return await action(client, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogInformation(ex, "Mail operation {Operation} cancelled for host {Host}.", operation, endpoint.Host);
            throw;
        }
        catch (Exception ex) when (ex is MailConnectionException or SmtpDeliveryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = MailConnectionErrorClassifier.Classify(ex);
            logger.LogError(ex,
                "Mail operation {Operation} failed ({Failure}) for host {Host} on port {Port}.",
                operation, failure, endpoint.Host, endpoint.Port);
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
                    logger.LogDebug(ex, "Mail disconnect failed for host {Host}.", endpoint.Host);
                }
            }
        }
    }

    private static SecureSocketOptions ToSocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => throw new ArgumentOutOfRangeException(nameof(security))
    };
}
