using System.Security.Cryptography;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Infrastructure.Services;

public sealed record MailReadOutcome(bool Found, bool Applied, bool Conflict, bool ProviderError);

public sealed class MailReadService(
    AppDbContext db,
    Mail.IMailFolderClient folders,
    AuditLogger audit,
    ILogger<MailReadService> logger)
{
    public Task<MailEntity?> GetAsync(Guid accountId, Guid mailId, CancellationToken cancellationToken) =>
        db.Mails.AsNoTracking().SingleOrDefaultAsync(mail => mail.Id == mailId && mail.MailAccountId == accountId, cancellationToken);

    public async Task<MailReadOutcome> SetReadAsync(
        Guid accountId,
        Guid mailId,
        bool isRead,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var mail = await db.Mails
            .Include(item => item.MailAccount)
            .Include(item => item.MailFolder)
            .SingleOrDefaultAsync(item => item.Id == mailId && item.MailAccountId == accountId, cancellationToken);
        if (mail?.MailAccount is null || mail.MailFolder is null || mail.MailAccount.Status != MailAccountStatus.Active)
            return new MailReadOutcome(false, false, false, false);

        if (mail.IsRead == isRead)
            return new MailReadOutcome(true, false, false, false);

        var account = mail.MailAccount;
        var fullName = mail.MailFolder.FullName;
        try
        {
            await folders.UseFolderAsync(
                account,
                fullName,
                forUpdate: true,
                async (remote, ct) =>
                {
                    if (remote.UidValidity != mail.UidValidity)
                        throw new MailboxStateChangedException();
                    await remote.SetSeenAsync(new UniqueId(mail.Uid), isRead, ct);
                    return true;
                },
                cancellationToken);
        }
        catch (MailboxStateChangedException)
        {
            logger.LogWarning("Read flag not applied for mail {MailId}: UIDVALIDITY changed on folder {FullName}.", mailId, fullName);
            return new MailReadOutcome(true, false, true, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or MailConnectionException)
        {
            logger.LogWarning(ex, "Read flag not applied for mail {MailId}.", mailId);
            return new MailReadOutcome(true, false, false, true);
        }

        mail.IsRead = isRead;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, "mail.read-state-changed", "Mail", mail.Id.ToString(),
            new Dictionary<string, string?> { ["isRead"] = isRead.ToString() }, correlationId, cancellationToken);
        return new MailReadOutcome(true, true, false, false);
    }

    private sealed class MailboxStateChangedException : Exception;
}
