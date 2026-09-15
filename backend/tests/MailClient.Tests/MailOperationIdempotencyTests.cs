using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class MailOperationIdempotencyTests
{
    [Fact]
    public async Task ExecuteAsync_StarAlreadyApplied_DoesNotCallRemote()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount { Id = accountId, EmailAddress = "a@test", NormalizedEmailAddress = "A@TEST", Username = "a", Status = MailAccountStatus.Active });
        db.MailFolders.Add(new MailFolder { Id = folderId, MailAccountId = accountId, FullName = "INBOX", FolderType = MailFolderType.Inbox, UidValidity = 7 });
        db.Mails.Add(new Mail { Id = mailId, MailAccountId = accountId, MailFolderId = folderId, Uid = 1, UidValidity = 7, Flagged = true });
        await db.SaveChangesAsync();
        var remote = new CountingRemote(7);
        var service = new MailOperationService(db, new FakeMailFolderClient(remote), new MailReadService(db, new FakeMailFolderClient(remote), new AuditLogger(db), NullLogger<MailReadService>.Instance), new AuditLogger(db), new InitialSyncQueue(), NullLogger<MailOperationService>.Instance);

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Star), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, remote.FlagCalls);
    }

    private sealed class CountingRemote(uint validity) : FakeRemoteMailFolder(validity, new())
    {
        public int FlagCalls { get; private set; }
        public override Task SetFlaggedAsync(MailKit.UniqueId uid, bool flagged, CancellationToken cancellationToken) { FlagCalls++; return Task.CompletedTask; }
    }
}
