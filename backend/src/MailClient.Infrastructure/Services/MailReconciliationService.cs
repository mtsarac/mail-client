using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed class MailReconciliationService(AppDbContext db)
{
    public async Task<bool> ReconcileAsync(Guid accountId, Guid destinationFolderId, string? messageId, uint destinationUid, uint destinationUidValidity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId) || destinationUid == 0 || destinationUidValidity == 0)
            return false;

        var matches = await db.Mails
            .Where(mail => mail.MailAccountId == accountId
                && mail.ExpectedMailFolderId == destinationFolderId
                && mail.ReconciliationState == MailReconciliationState.Pending
                && mail.MessageId == messageId)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (matches.Count != 1)
            return false;

        var mail = matches[0];
        mail.MailFolderId = destinationFolderId;
        mail.Uid = destinationUid;
        mail.UidValidity = destinationUidValidity;
        mail.ExpectedMailFolderId = null;
        mail.ReconciliationState = MailReconciliationState.None;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
