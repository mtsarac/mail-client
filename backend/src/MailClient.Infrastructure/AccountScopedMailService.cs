using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Mail;

public sealed class AccountScopedMailService(AppDbContext db)
{
    public Task<Domain.Entities.Mail?> GetAsync(Guid accountId, Guid mailId, CancellationToken cancellationToken) =>
        db.Mails.AsNoTracking().SingleOrDefaultAsync(mail => mail.Id == mailId && mail.MailAccountId == accountId, cancellationToken);

    public async Task<bool> SetReadAsync(Guid accountId, Guid mailId, bool isRead, CancellationToken cancellationToken)
    {
        var mail = await db.Mails.SingleOrDefaultAsync(item => item.Id == mailId && item.MailAccountId == accountId, cancellationToken);
        if (mail is null) return false;
        mail.IsRead = isRead;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
