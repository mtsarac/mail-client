using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Tests;

public sealed class MailReconciliationTests
{
    [Fact]
    public async Task ReconcileAsync_RebindsPendingMailWithoutCreatingDuplicate()
    {
        await using var db = CreateDb();
        var accountId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount { Id = accountId, EmailAddress = "a@test", NormalizedEmailAddress = "A@TEST", Username = "a", Status = MailAccountStatus.Active });
        db.MailFolders.AddRange(
            new MailFolder { Id = sourceId, MailAccountId = accountId, FullName = "INBOX", Name = "INBOX", FolderType = MailFolderType.Inbox, UidValidity = 1 },
            new MailFolder { Id = destinationId, MailAccountId = accountId, FullName = "Archive", Name = "Archive", FolderType = MailFolderType.Archive, UidValidity = 2 });
        db.Mails.Add(new Mail { Id = mailId, MailAccountId = accountId, MailFolderId = sourceId, PreviousMailFolderId = sourceId, ExpectedMailFolderId = destinationId, ReconciliationState = MailReconciliationState.Pending, MessageId = "<stable@test>", Uid = 4, UidValidity = 1 });
        await db.SaveChangesAsync();
        var service = new MailReconciliationService(db);

        var rebound = await service.ReconcileAsync(accountId, destinationId, "<stable@test>", 22, 2, CancellationToken.None);

        Assert.True(rebound);
        var mail = await db.Mails.SingleAsync(x => x.Id == mailId);
        Assert.Equal(destinationId, mail.MailFolderId);
        Assert.Equal(22u, mail.Uid);
        Assert.Equal(2u, mail.UidValidity);
        Assert.Equal(MailReconciliationState.None, mail.ReconciliationState);
        Assert.Null(mail.ExpectedMailFolderId);
        Assert.Single(await db.Mails.ToListAsync());
    }

    [Fact]
    public async Task ReconcileAsync_DuplicateOrMissingMessageId_DoesNotRebind()
    {
        await using var db = CreateDb();
        var accountId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount { Id = accountId, EmailAddress = "a@test", NormalizedEmailAddress = "A@TEST", Username = "a", Status = MailAccountStatus.Active });
        db.MailFolders.Add(new MailFolder { Id = destinationId, MailAccountId = accountId, FullName = "Archive", Name = "Archive", FolderType = MailFolderType.Archive });
        db.Mails.AddRange(
            new Mail { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = destinationId, ExpectedMailFolderId = destinationId, ReconciliationState = MailReconciliationState.Pending, MessageId = "<duplicate@test>" },
            new Mail { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = destinationId, ExpectedMailFolderId = destinationId, ReconciliationState = MailReconciliationState.Pending, MessageId = "<duplicate@test>" });
        await db.SaveChangesAsync();
        var service = new MailReconciliationService(db);

        Assert.False(await service.ReconcileAsync(accountId, destinationId, "<duplicate@test>", 1, 1, CancellationToken.None));
        Assert.False(await service.ReconcileAsync(accountId, destinationId, "", 1, 1, CancellationToken.None));
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
