using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Infrastructure.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;

// Opens validated IMAP/SMTP connections using SSRF-checked resolved addresses.
namespace MailClient.Infrastructure.Email;

public sealed class MailConnectionHelper(
    IOutboundHostValidator hosts,
    ILogger<MailConnectionHelper> logger)
{
    private const int OperationTimeoutMs = 30_000;

    internal async Task<T> WithImapAsync<T>(
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

    internal async Task<T> WithSmtpAsync<T>(
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
                MailSecurityMapper.ToSocketOptions(endpoint.Security), cancellationToken);
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
            throw new MailConnectionException(failure, MailConnectionErrorClassifier.SafeMessage(failure), ex);
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
}
