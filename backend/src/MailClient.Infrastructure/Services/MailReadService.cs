using System.Security.Cryptography;
using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// Applies read/unread changes to IMAP first, then to the local cache.
namespace MailClient.Infrastructure.Services;

public sealed class MailReadService(
    AppDbContext db,
    IMailFolderClient folders,
    ILogger<MailReadService> logger) : IMailReadService
{
    public async Task<ServiceResult<MailReadDto>> SetReadAsync(
        Guid userId,
        Guid mailId,
        bool isRead,
        CancellationToken cancellationToken)
    {
        var mail = await db.Mails
            .Include(item => item.MailAccount)
            .Include(item => item.MailFolder)
            .SingleOrDefaultAsync(
                item => item.Id == mailId && item.MailAccount!.UserId == userId,
                cancellationToken);
        if (mail?.MailAccount is null || mail.MailFolder is null || !mail.MailAccount.IsActive)
            return ServiceResult<MailReadDto>.Failure(ServiceOutcome.NotFound, "mail", "Mail not found.");

        if (mail.IsRead == isRead)
            return ServiceResult<MailReadDto>.Success(new MailReadDto(mail.Id, mail.IsRead));

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
            logger.LogWarning(
                "Read flag not applied for mail {MailId}: UIDVALIDITY changed on folder {FullName}.",
                mailId, fullName);
            return ServiceResult<MailReadDto>.Failure(
                ServiceOutcome.Conflict, "mail", "Mailbox folder changed. Sync will repair this folder.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or MailConnectionException)
        {
            logger.LogWarning(ex, "Read flag not applied for mail {MailId}.", mailId);
            return ServiceResult<MailReadDto>.Failure(
                ServiceOutcome.ProviderError, "mail", "Mail server operation failed.");
        }

        mail.IsRead = isRead;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<MailReadDto>.Success(new MailReadDto(mail.Id, mail.IsRead));
    }

    private sealed class MailboxStateChangedException : Exception;
}
