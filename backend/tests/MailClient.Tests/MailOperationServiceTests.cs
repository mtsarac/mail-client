using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Application.Sync;
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
    public async Task ExecuteAsync_MoveWithoutDestinationUidOrMessageId_LeavesRowForVanishCleanup()
    {
        await using var db = CreateDb();
        var (accountId, folderId, mailId) = await SeedAsync(db);
        var destinationId = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder { Id = destinationId, MailAccountId = accountId, Name = "Archive", FullName = "Archive", FolderType = MailFolderType.Archive, UidValidity = 8 });
        var seeded = await db.Mails.SingleAsync(x => x.Id == mailId);
        seeded.MessageId = "";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var scheduler = new FakeSyncScheduler();
        var service = CreateService(db, new FakeMailFolderClient(new NullDestinationRemote(7)), scheduler);

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Move, destinationId), null, CancellationToken.None);

        Assert.True(result.Success);
        var mail = await db.Mails.SingleAsync(x => x.Id == mailId);
        Assert.Equal(folderId, mail.MailFolderId);
        Assert.Equal(MailReconciliationState.None, mail.ReconciliationState);
        Assert.Null(mail.ExpectedMailFolderId);
        Assert.Contains(scheduler.Scheduled, item => item.FolderId == destinationId);
    }

    [Fact]
    public async Task ExecuteAsync_Restore_UsesRecordedPreviousFolder()
    {
        await using var db = CreateDb();
        var (accountId, folderId, mailId) = await SeedAsync(db);
        var trashId = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder { Id = trashId, MailAccountId = accountId, Name = "Trash", FullName = "Trash", FolderType = MailFolderType.Trash, UidValidity = 8 });
        var mail = await db.Mails.SingleAsync(x => x.Id == mailId);
        mail.MailFolderId = trashId;
        mail.PreviousMailFolderId = folderId;
        mail.UidValidity = 8;
        await db.SaveChangesAsync();
        var remote = new DestinationUidRemote(8, 12, 9);
        var service = CreateService(db, new FakeMailFolderClient(remote));

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Restore), null, CancellationToken.None);

        Assert.True(result.Success);
        var restored = await db.Mails.SingleAsync(x => x.Id == mailId);
        Assert.Equal(folderId, restored.MailFolderId);
        Assert.Equal(12u, restored.Uid);
        Assert.Equal(9u, restored.UidValidity);
        Assert.Null(restored.PreviousMailFolderId);
        Assert.NotNull(await db.AuditLogs.SingleOrDefaultAsync(x => x.Action == "mail.restore"));
    }

    [Fact]
    public async Task ExecuteAsync_MoveWithoutDestinationUid_MarksReconciliationPending()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = CreateDb(databaseName);
        var (accountId, folderId, mailId) = await SeedAsync(db);
        var destinationId = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder { Id = destinationId, MailAccountId = accountId, Name = "Archive", FullName = "Archive", FolderType = MailFolderType.Archive, UidValidity = 8 });
        await db.SaveChangesAsync();
        var persistedWhenScheduled = MailReconciliationState.None;
        var scheduler = new FakeSyncScheduler(() =>
        {
            // The coordinator reads through its own scope; the pending marker must already be committed.
            using var observer = CreateDb(databaseName);
            persistedWhenScheduled = observer.Mails.Single(x => x.Id == mailId).ReconciliationState;
        });
        var service = CreateService(db, new FakeMailFolderClient(new NullDestinationRemote(7)), scheduler);

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Move, destinationId), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.ReconciliationPending);
        var mail = await db.Mails.SingleAsync(x => x.Id == mailId);
        Assert.Equal(MailReconciliationState.Pending, mail.ReconciliationState);
        Assert.NotEqual(destinationId, mail.MailFolderId);
        Assert.Equal(MailReconciliationState.Pending, persistedWhenScheduled);
    }

    [Fact]
    public async Task ExecuteBulkAsync_MixedIds_AppliesEachIndependently()
    {
        await using var db = CreateDb();
        var (accountId, folderId, mailId) = await SeedAsync(db);
        var secondMailId = Guid.NewGuid();
        db.Mails.Add(new Mail { Id = secondMailId, MailAccountId = accountId, MailFolderId = folderId, Uid = 6, UidValidity = 7, Subject = "second" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var missingMailId = Guid.NewGuid();
        var remote = new RecordingRemoteFolder(7);
        var service = CreateService(db, new FakeMailFolderClient(remote));

        var result = await service.ExecuteBulkAsync(accountId, [mailId, missingMailId, secondMailId], MailOperationKind.Star, null, "corr", CancellationToken.None);

        Assert.Equal(3, result.Results.Count);
        Assert.True(result.Results.Single(item => item.MailId == mailId).Success);
        Assert.True(result.Results.Single(item => item.MailId == secondMailId).Success);
        var missing = result.Results.Single(item => item.MailId == missingMailId);
        Assert.False(missing.Success);
        Assert.Equal(MailOperationError.NotFound, missing.Error);
        // Both real mails were flagged even though the missing one failed in between.
        Assert.True((await db.Mails.SingleAsync(x => x.Id == mailId)).Flagged);
        Assert.True((await db.Mails.SingleAsync(x => x.Id == secondMailId)).Flagged);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteFromTrash_ExpungesRemoteThenRemovesRowAndFiles()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedAsync(db);
        await MoveToFolderAsync(db, mailId, MailFolderType.Trash);
        db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), MailAccountId = accountId, MailId = mailId, FileName = "a.txt", StoragePath = "stored/a.txt" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var remote = new FakeRemoteMailFolder(8, new());
        var storage = new FakeFileStorage();
        var push = new FakePushNotificationService();
        var service = CreateService(db, new FakeMailFolderClient(remote), storage: storage, push: push);

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Delete), "corr", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal([5u], remote.Expunged);
        Assert.False(await db.Mails.AnyAsync(x => x.Id == mailId));
        Assert.Equal(["stored/a.txt"], storage.Deleted);
        Assert.NotNull(await db.AuditLogs.SingleOrDefaultAsync(x => x.Action == "mail.delete"));
        Assert.Equal("delete", Assert.Single(push.Notifications).Operation);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteOutsideTrashOrJunk_IsNotSupportedAndLeavesServerUntouched()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedAsync(db);
        var remote = new FakeRemoteMailFolder(7, new());
        var service = CreateService(db, new FakeMailFolderClient(remote));

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Delete), null, CancellationToken.None);

        Assert.Equal(MailOperationError.NotSupported, result.Error);
        Assert.Empty(remote.Expunged);
        Assert.True(await db.Mails.AnyAsync(x => x.Id == mailId));
    }

    [Fact]
    public async Task ExecuteAsync_DeleteNotExpungedByServer_KeepsLocalRow()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedAsync(db);
        await MoveToFolderAsync(db, mailId, MailFolderType.Junk);
        var service = CreateService(db, new FakeMailFolderClient(new NotExpungingRemote(8)));

        var result = await service.ExecuteAsync(accountId, new(mailId, MailOperationKind.Delete), null, CancellationToken.None);

        Assert.Equal(MailOperationError.DeleteFailed, result.Error);
        Assert.True(await db.Mails.AnyAsync(x => x.Id == mailId));
    }

    [Fact]
    public async Task ExecuteBulkAsync_Delete_ReportsPerItemResults()
    {
        await using var db = CreateDb();
        var (accountId, folderId, trashedMailId) = await SeedAsync(db);
        await MoveToFolderAsync(db, trashedMailId, MailFolderType.Trash);
        var inboxMailId = Guid.NewGuid();
        db.Mails.Add(new Mail { Id = inboxMailId, MailAccountId = accountId, MailFolderId = folderId, Uid = 6, UidValidity = 7, Subject = "inbox" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var missingMailId = Guid.NewGuid();
        var remote = new FakeRemoteMailFolder(8, new());
        var service = CreateService(db, new FakeMailFolderClient(remote));

        var result = await service.ExecuteBulkAsync(accountId, [trashedMailId, inboxMailId, missingMailId], MailOperationKind.Delete, null, null, CancellationToken.None);

        Assert.Equal(
            [new(trashedMailId, true), new(inboxMailId, false, MailOperationError.NotSupported), new(missingMailId, false, MailOperationError.NotFound)],
            result.Results);
        Assert.Equal([5u], remote.Expunged);
        Assert.False(await db.Mails.AnyAsync(x => x.Id == trashedMailId));
        Assert.True(await db.Mails.AnyAsync(x => x.Id == inboxMailId));
    }

    private static MailOperationService CreateService(AppDbContext db, IMailFolderClient folders, FakeSyncScheduler? scheduler = null, FakeFileStorage? storage = null, FakePushNotificationService? push = null) =>
        new(db, folders, new MailReadService(db, folders, new AuditLogger(db), NullLogger<MailReadService>.Instance), new AuditLogger(db), scheduler ?? new FakeSyncScheduler(), NullLogger<MailOperationService>.Instance, push ?? new FakePushNotificationService(), storage ?? new FakeFileStorage());

    /// <summary>Puts the seeded mail into a new folder of <paramref name="type"/> with UIDVALIDITY 8.</summary>
    private static async Task MoveToFolderAsync(AppDbContext db, Guid mailId, MailFolderType type)
    {
        var mail = await db.Mails.SingleAsync(x => x.Id == mailId);
        var folder = new MailFolder { Id = Guid.NewGuid(), MailAccountId = mail.MailAccountId, Name = type.ToString(), FullName = type.ToString(), FolderType = type, UidValidity = 8 };
        db.MailFolders.Add(folder);
        mail.MailFolderId = folder.Id;
        mail.UidValidity = 8;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<(Guid AccountId, Guid FolderId, Guid MailId)> SeedAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount { Id = accountId, EmailAddress = "a@example.test", NormalizedEmailAddress = "A@EXAMPLE.TEST", Username = "a", Status = MailAccountStatus.Active });
        db.MailFolders.Add(new MailFolder { Id = folderId, MailAccountId = accountId, Name = "INBOX", FullName = "INBOX", FolderType = MailFolderType.Inbox, UidValidity = 7 });
        db.Mails.Add(new Mail { Id = mailId, MailAccountId = accountId, MailFolderId = folderId, Uid = 5, UidValidity = 7, Subject = "test", MessageId = "seed@example.test" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId, mailId);
    }

    private static AppDbContext CreateDb(string? name = null) => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    private sealed class RecordingRemoteFolder(uint uidValidity) : FakeRemoteMailFolder(uidValidity, new())
    {
        public List<string> Calls { get; } = [];
        public override Task SetFlaggedAsync(MailKit.UniqueId uid, bool flagged, CancellationToken cancellationToken) { Calls.Add($"flagged:{flagged.ToString().ToLowerInvariant()}"); return Task.CompletedTask; }
    }

    private sealed class DestinationUidRemote(uint sourceValidity, uint destinationUid, uint destinationValidity) : FakeRemoteMailFolder(sourceValidity, new())
    {
        private readonly uint _destinationValidity = destinationValidity;
        public override Task<RemoteMoveResult> MoveAsync(MailKit.UniqueId uid, string destinationFullName, CancellationToken cancellationToken) => Task.FromResult(new RemoteMoveResult(new(destinationUid), _destinationValidity));
    }

    private sealed class NullDestinationRemote(uint validity) : FakeRemoteMailFolder(validity, new()) { }

    private sealed class NotExpungingRemote(uint validity) : FakeRemoteMailFolder(validity, new())
    {
        public override Task<bool> ExpungeAsync(MailKit.UniqueId uid, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
