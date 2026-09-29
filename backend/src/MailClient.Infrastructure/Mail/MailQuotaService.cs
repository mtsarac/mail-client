using System.Security.Cryptography;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Security;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Mail;

public sealed record MailQuota(long UsedBytes, long LimitBytes);

public interface IMailQuotaService
{
    /// <summary>Returns the INBOX storage quota, or null when the server does not expose one or cannot be asked.</summary>
    Task<MailQuota?> GetAsync(MailAccount account, CancellationToken cancellationToken);
}

public sealed class MailQuotaService(
    MailCredentialResolver credentials,
    MailConnectionHelper connections,
    ILogger<MailQuotaService> logger) : IMailQuotaService
{
    public async Task<MailQuota?> GetAsync(MailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await credentials.ResolveAsync(account.Id, cancellationToken);
            var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
            return await connections.WithImapAsync(endpoint, resolved.Username, resolved.Secret, "GetQuota",
                ReadAsync, cancellationToken, resolved.AuthenticationMethod);
        }
        // Quota is informational: unreachable servers, rejected credentials and accounts awaiting
        // reauthentication all surface as "unavailable" instead of failing the settings screen.
        catch (Exception ex) when (ex is MailConnectionException or InvalidOperationException or CryptographicException)
        {
            logger.LogInformation("Quota unavailable for account {AccountId}: {Reason}.", account.Id,
                ex is MailConnectionException connection ? connection.Failure.ToString() : ex.Message);
            return null;
        }
    }

    internal static async Task<MailQuota?> ReadAsync(ImapClient imap, CancellationToken cancellationToken)
    {
        if (!imap.Capabilities.HasFlag(ImapCapabilities.Quota)) return null;

        FolderQuota quota;
        try
        {
            quota = await imap.Inbox.GetQuotaAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is ImapCommandException or NotSupportedException)
        {
            return null;
        }

        // RFC 9208 STORAGE is counted in units of 1024 octets; MailKit exposes it unchanged as kilobytes.
        if (quota.StorageLimit is not { } limit || limit == 0 || quota.CurrentStorageSize is not { } used) return null;
        return new MailQuota(used * 1024L, limit * 1024L);
    }
}
