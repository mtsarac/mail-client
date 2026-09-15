using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class MailOperationServiceTests
{
    [Fact]
    public async Task ExecuteAsync_DifferentAccount_ReturnsNotFoundWithoutRemoteCall()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedAsync(db);
        var remote = new FakeRemoteMailFolder(7, new());
        var service = CreateService(db, new FakeMailFolderClient(remote));

        var result = await service.ExecuteAsync(Guid.NewGuid(), new(mailId, MailOperationKind.Star), null, CancellationToken.None);

        Assert.Equal(MailOperationError.NotFound, result.Error);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_Star_RemoteFirstThenUpdatesCache()
    {
        await using var db = CreateDb();
        var (accountId, folderId, mailId) = await SeedAsync(db);
        var remote = new RecordingRemoteFolder(7);
        var service = CreateService(db, new FakeMailFolderClient(remote));

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Star), "corr", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["flagged:true"], remote.Calls);
        Assert.True((await db.Mails.SingleAsync(x => x.Id == mailId)).Flagged);
        Assert.NotNull(await db.AuditLogs.SingleOrDefaultAsync(x => x.Action == "mail.star"));
    }

    [Fact]
    public async Task ExecuteAsync_UidValidityConflict_DoesNotChangeCache()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedAsync(db);
        var remote = new RecordingRemoteFolder(99);
        var service = CreateService(db, new FakeMailFolderClient(remote));

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Star), null, CancellationToken.None);

        Assert.Equal(MailOperationError.Conflict, result.Error);
        Assert.False((await db.Mails.SingleAsync(x => x.Id == mailId)).Flagged);
        Assert.Empty(remote.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_MoveWithoutDestinationUid_RequiresReconciliationAndDoesNotChangeCache()
    {
        await using var db = CreateDb();
        var (accountId, folderId, mailId) = await SeedAsync(db);
        var destinationId = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder { Id = destinationId, MailAccountId = accountId, Name = "Archive", FullName = "Archive", FolderType = MailFolderType.Archive, UidValidity = 8 });
        await db.SaveChangesAsync();
        var remote = new RecordingRemoteFolder(7);
        var service = CreateService(db, new FakeMailFolderClient(remote));

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Move, destinationId), null, CancellationToken.None);

        Assert.Equal(MailOperationError.MoveFailed, result.Error);
        Assert.True(result.DestinationReconciliationRequired);
        var mail = await db.Mails.SingleAsync(x => x.Id == mailId);
        Assert.Equal(folderId, mail.MailFolderId);
    }

    private static MailOperationService CreateService(AppDbContext db, IMailFolderClient folders) =>
        new(db, folders, new MailReadService(db, folders, new AuditLogger(db), NullLogger<MailReadService>.Instance), new AuditLogger(db), NullLogger<MailOperationService>.Instance);

    private static async Task<(Guid AccountId, Guid FolderId, Guid MailId)> SeedAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount { Id = accountId, EmailAddress = "a@example.test", NormalizedEmailAddress = "A@EXAMPLE.TEST", Username = "a", Status = MailAccountStatus.Active });
        db.MailFolders.Add(new MailFolder { Id = folderId, MailAccountId = accountId, Name = "INBOX", FullName = "INBOX", FolderType = MailFolderType.Inbox, UidValidity = 7 });
        db.Mails.Add(new Mail { Id = mailId, MailAccountId = accountId, MailFolderId = folderId, Uid = 5, UidValidity = 7, Subject = "test" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId, mailId);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RecordingRemoteFolder(uint uidValidity) : FakeRemoteMailFolder(uidValidity, new())
    {
        public List<string> Calls { get; } = [];
        public override Task SetFlaggedAsync(MailKit.UniqueId uid, bool flagged, CancellationToken cancellationToken) { Calls.Add($"flagged:{flagged.ToString().ToLowerInvariant()}"); return Task.CompletedTask; }
    }
}
