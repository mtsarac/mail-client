using MailClient.Application.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static MailClient.Tests.SyncTestSeed;

namespace MailClient.Tests;

public sealed class SyncServiceTests
{
    private static RuntimeSettings Options(int maxMessages) =>
        TestServices.SyncSettings(maxMessages, flagSyncIntervalSeconds: 3600, maxAttachmentBytes: 500, maxMessageAttachmentBytes: 800, maxMessageBytes: 100000);

    [Fact]
    public async Task HugeBacklog_SearchIsBounded_FirstRunEndsAtCorrectCheckpoint()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var messages = Enumerable.Range(1, 10000)
            .ToDictionary(uid => (uint)uid, uid => (Func<MimeKit.MimeMessage>)(() => SimpleMessage($"m{uid}")));
        var remote = new FakeRemoteMailFolder(7, messages);
        var service = CreateService(db, Options(100));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(100, remote.LastSearchMaxCount);
        Assert.Equal(100, await db.Mails.CountAsync());
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task Backlog_ResumesAcrossRuns_WithoutGapsOrDuplicates()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var messages = Enumerable.Range(1, 250)
            .ToDictionary(uid => (uint)uid, uid => (Func<MimeKit.MimeMessage>)(() => SimpleMessage($"m{uid}")));
        var remote = new FakeRemoteMailFolder(7, messages);
        var service = CreateService(db, Options(100));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(250, await db.Mails.CountAsync());
        Assert.Equal(250, await db.Mails.Select(item => item.Uid).Distinct().CountAsync());
        Assert.Equal(250u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task UidValidityReset_ClearsCacheAndResetsCursor()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var state = await db.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
        state.UidValidity = 6;
        state.LastUid = 500;
        state.NextUidScanStart = 999;
        state.BackfillNextUid = 400;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeKit.MimeMessage>>
        {
            [600] = () => SimpleMessage("after-reset")
        });

        await CreateService(db, Options(100)).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var updated = await db.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
        Assert.Equal(7u, updated.UidValidity);
        Assert.Equal(600u, updated.LastUid);
        Assert.Equal(601L, updated.NextUidScanStart);
        Assert.Single(await db.Mails.ToListAsync());
        Assert.Equal(0, updated.BackfillNextUid);
        Assert.Equal(0, remote.LastBackfillBelowUid);
    }

    [Fact]
    public async Task ForwardSearch_WithoutServerUidNext_StillFindsNewMessages()
    {
        uint[] remoteUids = [5, 11, 12, 13];
        var result = await Infrastructure.Email.MailKitRemoteMailFolder.SearchPagedAsync(10, 2, () => 0,
            (low, high, _) => Task.FromResult<IList<MailKit.UniqueId>>(
                [.. remoteUids.Where(uid => uid >= low && uid <= high).Select(uid => new MailKit.UniqueId(uid))]),
            CancellationToken.None);

        Assert.Equal([11u, 12u], result.Uids.Select(uid => uid.Id));
        Assert.Equal(12u, result.ScannedUpTo);
    }

    [Fact]
    public async Task OversizedMessage_IsSkipped_CheckpointAdvances()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeKit.MimeMessage>>
        {
            [1] = () => SimpleMessage("too-big"),
            [2] = () => SimpleMessage("fine")
        }, sizes: new Dictionary<uint, uint> { [1] = 200000, [2] = 100 });
        var service = CreateService(db, Options(100));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Single(await db.SyncSkippedUids.ToListAsync());
        Assert.Equal(2u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task ServerSeenFlag_ReconcilesLocalReadState()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeKit.MimeMessage>>
        {
            [1] = () => SimpleMessage("flagged")
        }, seenUids: [1]);
        var service = CreateService(db, Options(100));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.True((await db.Mails.SingleAsync()).IsRead);
    }

    [Fact]
    public async Task NewInboxMail_NotifiesWithOwningAccount()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeKit.MimeMessage>>
        {
            [1] = () => SimpleMessage("push-me")
        });
        var push = new FakePushNotificationService();
        var service = CreateService(db, Options(100), push);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var notification = Assert.Single(push.Notifications);
        Assert.Equal(accountId, notification.MailAccountId);
    }

    [Theory]
    [InlineData(0u, 10u, false)]
    [InlineData(10u, 10u, false)]
    [InlineData(10u, 11u, true)]
    public void RequiresReset_ReturnsTrueOnlyForChangedKnownUidValidity(
        uint storedUidValidity,
        uint serverUidValidity,
        bool expected)
    {
        Assert.Equal(expected, SyncStateDecision.RequiresReset(storedUidValidity, serverUidValidity));
    }

    [Fact]
    public async Task NewMailbox_ImportsNewestFirst_ThenBackfillsOlderHistory()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedUnsyncedFolderAsync(db);
        var messages = Enumerable.Range(1, 1000)
            .ToDictionary(uid => (uint)uid, uid => (Func<MimeKit.MimeMessage>)(() => SimpleMessage($"m{uid}")));
        var remote = new FakeRemoteMailFolder(7, messages) { UidNext = 1001 };
        var service = CreateService(db, Options(100));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var firstRun = await db.Mails.Select(mail => mail.Uid).OrderBy(uid => uid).ToListAsync();
        Assert.Equal(100, firstRun.Count);
        Assert.Equal(901u, firstRun[0]);
        Assert.Equal(1000u, firstRun[^1]);
        var state = await db.SyncStates.SingleAsync();
        Assert.Equal(901, state.BackfillNextUid);
        Assert.Equal(1001, state.NextUidScanStart);

        for (var run = 0; run < 9; run++)
            await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(1000, await db.Mails.CountAsync());
        Assert.Equal(1000, await db.Mails.Select(mail => mail.Uid).Distinct().CountAsync());
        Assert.Equal(0, (await db.SyncStates.SingleAsync()).BackfillNextUid);
    }

    [Fact]
    public async Task BackfillInProgress_StillImportsNewArrivalsFirst()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedUnsyncedFolderAsync(db);
        var messages = Enumerable.Range(1, 500)
            .ToDictionary(uid => (uint)uid, uid => (Func<MimeKit.MimeMessage>)(() => SimpleMessage($"m{uid}")));
        var remote = new FakeRemoteMailFolder(7, messages) { UidNext = 501 };
        var service = CreateService(db, Options(10));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        remote.Messages[501] = () => SimpleMessage("brand-new");
        remote.UidNext = 502;
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Contains(501u, await db.Mails.Select(mail => mail.Uid).ToListAsync());
        Assert.True((await db.SyncStates.SingleAsync()).BackfillNextUid > 0, "older history must still be pending");
        Assert.Equal(
            await db.Mails.Select(mail => mail.Uid).Distinct().CountAsync(),
            await db.Mails.CountAsync());
    }

    [Fact]
    public async Task RemoteExpungeAndFlagChanges_ConvergeLocally_WithoutTouchingPendingMoves()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var messages = Enumerable.Range(1, 4)
            .ToDictionary(uid => (uint)uid, uid => (Func<MimeKit.MimeMessage>)(() => SimpleMessage($"m{uid}")));
        var remote = new FakeRemoteMailFolder(7, messages);
        var service = CreateService(db, Options(100));
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        Assert.Equal(4, await db.Mails.CountAsync());

        // UID 3 is mid-move locally; UID 2 and UID 4 disappear from the server.
        var pending = await db.Mails.SingleAsync(mail => mail.Uid == 3);
        pending.ReconciliationState = MailReconciliationState.Pending;
        pending.ExpectedMailFolderId = Guid.NewGuid();
        var state = await db.SyncStates.SingleAsync();
        state.LastFlagSyncAt = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        remote.Messages.Remove(2);
        remote.Messages.Remove(3);
        remote.Messages.Remove(4);
        await remote.SetSeenAsync(new MailKit.UniqueId(1), true, CancellationToken.None);
        remote.FlaggedUids.Add(1);
        remote.AnsweredUids.Add(1);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var remaining = await db.Mails.OrderBy(mail => mail.Uid).ToListAsync();
        Assert.Equal([1u, 3u], remaining.Select(mail => mail.Uid));
        Assert.True(remaining[0].IsRead);
        Assert.True(remaining[0].Flagged);
        Assert.True(remaining[0].Answered);
        var after = await db.SyncStates.SingleAsync();
        Assert.Equal(0u, after.FlagScanCursorUid);
        Assert.NotNull(after.LastFlagSyncAt);
    }

    [Fact]
    public async Task ConversationAssignmentFailure_KeepsImportedMailAndItsAttachments()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(new FailConversationInsertInterceptor())
            .Options);
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new() { [1] = () => MessageWithAttachment("with file"), [2] = () => SimpleMessage("next") });
        var storage = new FakeFileStorage();
        var service = CreateService(db, Options(100), storage: storage);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(2, await db.Mails.CountAsync());
        var attachment = await db.Attachments.SingleAsync();
        Assert.True(storage.Content.ContainsKey(attachment.StoragePath));
        Assert.Empty(storage.Deleted);
        Assert.Equal(2u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    private static MimeKit.MimeMessage MessageWithAttachment(string subject)
    {
        var message = SimpleMessage(subject);
        var body = new MimeKit.BodyBuilder { TextBody = "hello" };
        body.Attachments.Add("note.txt", System.Text.Encoding.UTF8.GetBytes("file"));
        message.Body = body.ToMessageBody();
        return message;
    }

    private sealed class FailConversationInsertInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Conversation>().Any(entry => entry.State == EntityState.Added))
                throw new DbUpdateException("conversation insert failed");
            return ValueTask.FromResult(result);
        }
    }

    private static MailFolderSyncService CreateService(AppDbContext db, RuntimeSettings options, FakePushNotificationService? push = null, FakeFileStorage? storage = null) =>
        new(db,
            TestServices.Credentials(db),
            new Infrastructure.Mail.MailConnectionHelper(
                new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)),
                NullLogger<Infrastructure.Mail.MailConnectionHelper>.Instance),
            storage ?? new FakeFileStorage(),
            FixedRuntimeSettingsStore.Operation(options),
            push ?? new FakePushNotificationService(),
            new MailClient.Infrastructure.Services.ConversationService(db), new MailReconciliationService(db), NullLogger<MailFolderSyncService>.Instance);

    private static async Task<(Guid AccountId, Guid FolderId)> SeedUnsyncedFolderAsync(AppDbContext db)
    {
        var seeded = await SeedFolderAsync(db);
        db.SyncStates.RemoveRange(db.SyncStates);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return seeded;
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
