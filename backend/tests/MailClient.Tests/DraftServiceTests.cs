using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Sync;
using MailClient.Application.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UniqueId = MailKit.UniqueId;
using MimeKit;

namespace MailClient.Tests;

public sealed class DraftServiceTests
{
    [Fact]
    public async Task CreateAsync_UsesDiscoveredDraftsFolderWithDraftFlag()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        var remote = new FakeRemoteMailFolder(31, new()) { AppendResult = new(new MailKit.UniqueId(8), 31) };
        var sync = new RecordingSyncExecutor();
        var service = CreateService(db, remote, sync);

        var result = await service.CreateAsync(accountId, Command(accountId), null, CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("Drafts", remote.LastFolderName);
        Assert.Equal(MailKit.MessageFlags.Draft, remote.LastAppendFlags);
        Assert.Equal(draftsId, sync.FolderId);
    }

    [Fact]
    public async Task CreateAsync_AllowsEmptyRecipientsAndBody()
    {
        await using var db = CreateDb();
        var (accountId, _) = await SeedAsync(db);
        var remote = new FakeRemoteMailFolder(31, new());
        var service = CreateService(db, remote, new RecordingSyncExecutor());
        var command = new DraftCommand(accountId, [], [], [], "", null, null, [], null);

        var result = await service.CreateAsync(accountId, command, null, CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(MailKit.MessageFlags.Draft, remote.LastAppendFlags);
    }

    [Fact]
    public async Task UpdateAsync_NoAppendUidAndSameMessageId_DoesNotResolveSourceDraft()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        AddTrash(db, accountId);
        var draftId = await SeedDraftAsync(db, accountId, draftsId);
        var remote = new FakeRemoteMailFolder(31, new()) { AppendResult = new(null, 31) };
        var sync = new RecordingSyncExecutor(async () =>
        {
            db.Mails.Add(new MailClient.Domain.Entities.Mail
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                MailFolderId = draftsId,
                Uid = 6,
                UidValidity = 31,
                Draft = true,
                MessageId = "draft@example.test",
                Subject = "replacement",
                InternalDate = DateTime.UtcNow
            });
            db.Mails.Add(new MailClient.Domain.Entities.Mail
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                MailFolderId = draftsId,
                Uid = 7,
                UidValidity = 31,
                Draft = true,
                MessageId = "draft@example.test",
                Subject = "ambiguous",
                InternalDate = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });
        var service = CreateService(db, remote, sync);

        var result = await service.UpdateAsync(accountId, draftId, Command(accountId), null, CancellationToken.None);

        Assert.True(result.ReconciliationPending);
        Assert.Null(result.MailId);
        // Only the source draft (UID 5) is retired; neither ambiguous copy is touched.
        Assert.Equal([5u], remote.Moved);
    }

    [Fact]
    public async Task UpdateAsync_InlineSyncBusy_StillRetiresSourceDraft()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        AddTrash(db, accountId);
        var draftId = await SeedDraftAsync(db, accountId, draftsId);
        var remote = new FakeRemoteMailFolder(31, new()) { AppendResult = new(new UniqueId(6), 31) };
        var sync = new RecordingSyncExecutor();
        var service = CreateService(db, remote, sync);
        // Another sync (e.g. the one queued by a previous PUT) owns the account, so the new copy cannot be imported inline.
        await using var busy = await new InMemorySyncLockProvider().TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, CancellationToken.None);

        var result = await service.UpdateAsync(accountId, draftId, Command(accountId), null, CancellationToken.None);

        Assert.True(result is { ReconciliationPending: true, MailId: null });
        Assert.Null(sync.FolderId);
        Assert.Equal([5u], remote.Moved);
    }

    [Fact]
    public async Task UpdateAsync_AppendFailure_LeavesOriginalDraftUntouched()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        var draftId = await SeedDraftAsync(db, accountId, draftsId);
        var remote = new FakeRemoteMailFolder(31, new()) { AppendFailure = new InvalidOperationException("append_failed") };
        var service = CreateService(db, remote, new RecordingSyncExecutor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(accountId, draftId, Command(accountId), null, CancellationToken.None));

        Assert.NotNull(await db.Mails.SingleOrDefaultAsync(mail => mail.Id == draftId));
        Assert.Empty(remote.Moved);
    }

    [Fact]
    public async Task DeleteAsync_RemoteConflict_LeavesFlaglessDraftListedAndEditable()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        AddTrash(db, accountId);
        var draftId = await SeedDraftAsync(db, accountId, draftsId, draft: false);
        var remote = new FakeRemoteMailFolder(99, new());
        var service = CreateService(db, remote, new RecordingSyncExecutor());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(accountId, draftId, null, CancellationToken.None));
        var listed = await new MailSearchService(db, FixedRuntimeSettingsStore.Operation()).SearchAsync(accountId,
            new MailSearchRequest(null, draftsId, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None);

        Assert.Equal("draft_delete_failed", error.Message);
        Assert.Empty(remote.Moved);
        Assert.Equal(draftId, Assert.Single(listed.Items).Id);
        Assert.Equal(DraftLookupError.None, (await service.GetAsync(accountId, draftId, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task GetAsync_ForeignAccountAndNonDraft_AreRejected()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        AddTrash(db, accountId);
        var trash = db.MailFolders.Local.Single(folder => folder.FolderType == MailFolderType.Trash);
        var mailId = await SeedDraftAsync(db, accountId, trash.Id);
        var service = CreateService(db, new FakeRemoteMailFolder(31, new()), new RecordingSyncExecutor());

        var other = await service.GetAsync(Guid.NewGuid(), mailId, CancellationToken.None);
        var nonDraft = await service.GetAsync(accountId, mailId, CancellationToken.None);

        Assert.Equal(DraftLookupError.NotFound, other.Error);
        Assert.Equal(DraftLookupError.NotDraft, nonDraft.Error);
        var update = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(accountId, mailId, Command(accountId), null, CancellationToken.None));
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(accountId, mailId, null, CancellationToken.None));
        Assert.Equal("mail_not_draft", update.Message);
        Assert.Equal("mail_not_draft", delete.Message);
    }

    [Fact]
    public async Task GetAsync_DraftsFolderWithoutDraftFlag_ReturnsDraft()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        var draftId = await SeedDraftAsync(db, accountId, draftsId, draft: false);
        var service = CreateService(db, new FakeRemoteMailFolder(31, new()), new RecordingSyncExecutor());

        var result = await service.GetAsync(accountId, draftId, CancellationToken.None);

        Assert.Equal(DraftLookupError.None, result.Error);
        Assert.Equal(draftId, result.Draft!.Id);
    }

    [Fact]
    public async Task UpdateAsync_WithoutDraftFlags_ListsOnlyEditableReplacementAfterMoveWithoutUid()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        AddTrash(db, accountId);
        var draftId = await SeedDraftAsync(db, accountId, draftsId, draft: false);
        var replacementId = Guid.NewGuid();
        var remote = new FakeRemoteMailFolder(31, new()) { AppendResult = new(new UniqueId(6), 31) };
        var sync = new RecordingSyncExecutor(async () =>
        {
            db.Mails.Add(new MailClient.Domain.Entities.Mail
            {
                Id = replacementId,
                MailAccountId = accountId,
                MailFolderId = draftsId,
                Uid = 6,
                UidValidity = 31,
                Draft = false,
                MessageId = "draft@example.test",
                Subject = "replacement"
            });
            await db.SaveChangesAsync();
        });
        var service = CreateService(db, remote, sync);

        var result = await service.UpdateAsync(accountId, draftId, Command(accountId), null, CancellationToken.None);
        var listed = await new MailSearchService(db, FixedRuntimeSettingsStore.Operation()).SearchAsync(accountId,
            new MailSearchRequest(null, draftsId, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None);
        var original = await service.GetAsync(accountId, draftId, CancellationToken.None);
        var replacement = await service.GetAsync(accountId, replacementId, CancellationToken.None);

        Assert.Equal(replacementId, result.MailId);
        Assert.False(result.ReconciliationPending);
        Assert.Equal(replacementId, Assert.Single(listed.Items).Id);
        Assert.Equal(1, listed.Total);
        Assert.Equal(DraftLookupError.NotDraft, original.Error);
        Assert.Equal(DraftLookupError.None, replacement.Error);
        Assert.Equal(replacementId, replacement.Draft!.Id);
        Assert.Equal([5u], remote.Moved);
        var staleDelete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(accountId, draftId, null, CancellationToken.None));
        var staleUpdate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(accountId, draftId, Command(accountId), null, CancellationToken.None));
        Assert.Equal("mail_not_draft", staleDelete.Message);
        Assert.Equal("mail_not_draft", staleUpdate.Message);
        Assert.Equal([5u], remote.Moved);
    }

    [Fact]
    public async Task DeleteAsync_WithoutDraftFlag_RetiresDraftAndRejectsForeignAccount()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        AddTrash(db, accountId);
        var draftId = await SeedDraftAsync(db, accountId, draftsId, draft: false);
        var remote = new FakeRemoteMailFolder(31, new());
        var service = CreateService(db, remote, new RecordingSyncExecutor());

        var foreignDelete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(Guid.NewGuid(), draftId, null, CancellationToken.None));
        var foreignUpdate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(Guid.NewGuid(), draftId, Command(accountId), null, CancellationToken.None));
        Assert.Equal("draft_not_found", foreignDelete.Message);
        Assert.Equal("draft_not_found", foreignUpdate.Message);
        Assert.Empty(remote.Moved);

        await service.DeleteAsync(accountId, draftId, null, CancellationToken.None);
        var listed = await new MailSearchService(db, FixedRuntimeSettingsStore.Operation()).SearchAsync(accountId,
            new MailSearchRequest(null, draftsId, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None);

        Assert.Equal([5u], remote.Moved);
        Assert.Empty(listed.Items);
        Assert.Equal(0, listed.Total);
        Assert.Equal(DraftLookupError.NotDraft, (await service.GetAsync(accountId, draftId, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task SendAsync_RetryWithSameKeyAfterDraftMovedToTrash_ReplaysWithoutResending()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        db.MailFolders.Add(new MailFolder { Id = Guid.NewGuid(), MailAccountId = accountId, Name = "Trash", FullName = "Trash", FolderType = MailFolderType.Trash, IsAvailable = true, UidValidity = 40 });
        var draftId = await SeedDraftAsync(db, accountId, draftsId);
        db.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = draftId, Type = ParticipantType.To, Address = "to@example.test", NormalizedAddress = "to@example.test" });
        var draft = await db.Mails.SingleAsync(x => x.Id == draftId);
        draft.BodyText = "body";
        await db.SaveChangesAsync();
        var transport = new FakeMailTransport();
        var service = CreateService(db, new UidPlusRemoteMailFolder(), new RecordingSyncExecutor(), transport);

        var first = await service.SendAsync(accountId, draftId, "draft-send-1", false, null, CancellationToken.None);
        var retry = await service.SendAsync(accountId, draftId, "draft-send-1", false, null, CancellationToken.None);

        Assert.True(first is { Sent: true, DraftRemoved: true });
        Assert.True(retry is { Sent: true, DraftRemoved: true });
        Assert.Equal(1, transport.SentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_RequestsReadReceiptOnlyWhenOptedIn(bool requestReadReceipt)
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        db.MailFolders.Add(new MailFolder { Id = Guid.NewGuid(), MailAccountId = accountId, Name = "Trash", FullName = "Trash", FolderType = MailFolderType.Trash, IsAvailable = true, UidValidity = 40 });
        var draftId = await SeedDraftAsync(db, accountId, draftsId);
        db.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = draftId, Type = ParticipantType.To, Address = "to@example.test", NormalizedAddress = "to@example.test" });
        var draft = await db.Mails.SingleAsync(x => x.Id == draftId);
        draft.BodyText = "body";
        await db.SaveChangesAsync();
        var transport = new FakeMailTransport();
        var service = CreateService(db, new UidPlusRemoteMailFolder(), new RecordingSyncExecutor(), transport);

        var result = await service.SendAsync(accountId, draftId, "draft-receipt", requestReadReceipt, null, CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Equal(requestReadReceipt, transport.Message!.Headers.Contains("Disposition-Notification-To"));
    }

    private static DraftCommand Command(Guid accountId) => new(
        accountId,
        ["to@example.test"],
        [],
        ["bcc@example.test"],
        "subject",
        "<p>html</p>",
        "text",
        [],
        null);

    private static DraftService CreateService(AppDbContext db, FakeRemoteMailFolder remote, RecordingSyncExecutor sync, FakeMailTransport? transport = null)
    {
        var folders = new RecordingMailFolderClient(remote);
        var audit = new AuditLogger(db);
        var reader = new MailReadService(db, folders, audit, NullLogger<MailReadService>.Instance);
        var operations = new MailOperationService(db, folders, reader, audit, new FakeSyncScheduler(), NullLogger<MailOperationService>.Instance, new FakePushNotificationService(), new FakeFileStorage(), TestServices.InlineSync(sync));
        var sendOperations = new SendOperationStore(db, NullLogger<SendOperationStore>.Instance);
        var inlineSync = TestServices.InlineSync(sync);
        var sender = new MailSendService(db, transport ?? new FakeMailTransport(), sendOperations, FixedRuntimeSettingsStore.Operation(), inlineSync, audit, NullLogger<MailSendService>.Instance);
        return new(db, folders, inlineSync, reader, operations, sender, sendOperations, new FakeFileStorage(), audit, NullLogger<DraftService>.Instance);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static void AddTrash(AppDbContext db, Guid accountId) =>
        db.MailFolders.Add(new MailFolder { Id = Guid.NewGuid(), MailAccountId = accountId, Name = "Trash", FullName = "Trash", FolderType = MailFolderType.Trash, IsAvailable = true, UidValidity = 40 });

    private static async Task<(Guid AccountId, Guid DraftsId)> SeedAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var draftsId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "me@example.test",
            NormalizedEmailAddress = $"ME-{accountId:N}@EXAMPLE.TEST",
            DisplayName = "Me",
            Username = "me",
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailFolder { Id = draftsId, MailAccountId = accountId, Name = "Drafts", FullName = "Drafts", FolderType = MailFolderType.Drafts, IsAvailable = true, IsSyncEnabled = true, UidValidity = 31 });
        await db.SaveChangesAsync();
        return (accountId, draftsId);
    }

    private static async Task<Guid> SeedDraftAsync(AppDbContext db, Guid accountId, Guid folderId, bool draft = true)
    {
        var id = Guid.NewGuid();
        db.Mails.Add(new MailClient.Domain.Entities.Mail { Id = id, MailAccountId = accountId, MailFolderId = folderId, Uid = 5, UidValidity = 31, Draft = draft, MessageId = "draft@example.test", Subject = "old" });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class RecordingSyncExecutor(Func<Task>? onSync = null) : ISyncExecutor
    {
        public Guid? FolderId { get; private set; }
        public async Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            FolderId = folderId;
            if (onSync is not null)
                await onSync();
        }
    }

    /// <summary>Server with UIDPLUS: MOVE reports the destination UID, so the draft leaves Drafts immediately.</summary>
    private sealed class UidPlusRemoteMailFolder() : FakeRemoteMailFolder(31, new())
    {
        public override Task<RemoteMoveResult> MoveAsync(UniqueId uid, string destinationFullName, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteMoveResult(new UniqueId(900), 40));
    }

    private sealed class RecordingMailFolderClient(FakeRemoteMailFolder remote) : IMailFolderClient
    {
        public Task<T> UseFolderAsync<T>(MailAccount account, string fullName, bool forUpdate, Func<IRemoteMailFolder, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        {
            remote.LastFolderName = fullName;
            return action(remote, cancellationToken);
        }
    }
}
