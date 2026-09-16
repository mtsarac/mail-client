using MailClient.Application.Sync;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Mail;

public sealed class MailFolderAccessService(AppDbContext db)
{
    public Task<bool> OwnsFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) =>
        db.MailFolders.AnyAsync(x => x.Id == folderId && x.MailAccountId == accountId, cancellationToken);

    public Task<MailFolderSyncAvailability?> GetFolderSyncAvailabilityAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) =>
        db.MailFolders
            .Where(x => x.Id == folderId && x.MailAccountId == accountId)
            .Select(x => new MailFolderSyncAvailability(x.IsAvailable))
            .SingleOrDefaultAsync(cancellationToken);
}

public sealed record MailFolderSyncAvailability(bool IsAvailable);
