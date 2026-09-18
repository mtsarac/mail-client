using MailClient.Application.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Accounts;

/// <summary>Single place that deletes a mailbox and its attachment storage, shared by the
/// self-service DELETE /api/account endpoint and the allowlist grace-period cleanup job.</summary>
public sealed class AccountDeletionService(AppDbContext db, IFileStorage storage, ILogger<AccountDeletionService> logger)
{
    public async Task<bool> DeleteAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts.FindAsync([accountId], cancellationToken);
        if (account is null)
            return false;
        db.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await storage.DeleteAccountAsync(accountId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Attachment cleanup failed after mailbox deletion.");
        }

        return true;
    }
}
