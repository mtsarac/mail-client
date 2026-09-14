using MailClient.Application.Sync;
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
    private static MailSyncOptions Options(int maxMessages) => new()
    {
        Enabled = true,
        PollIntervalSeconds = 30,
        FlagSyncIntervalSeconds = 3600,
        MaxMessagesPerRun = maxMessages,
        MaxAttachmentBytes = 500,
        MaxMessageAttachmentBytes = 800,
        MaxMessageBytes = 100000
    };

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

    [Fact]
    public async Task TargetedFolderSelection_IncludesDisabledAvailableFolder()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var folder = await db.MailFolders.SingleAsync(x => x.Id == folderId);
        folder.IsSyncEnabled = false;
        await db.SaveChangesAsync();

        var folders = await CreateService(db, Options(100)).GetSyncableFolderAsync(accountId, folderId, CancellationToken.None);

        Assert.Equal((folderId, "INBOX"), folders);
    }

    [Fact]
    public async Task BackgroundFolderSelection_ExcludesDisabledFolder()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var folder = await db.MailFolders.SingleAsync(x => x.Id == folderId);
        folder.IsSyncEnabled = false;
        await db.SaveChangesAsync();

        var folders = await CreateService(db, Options(100)).GetSyncableFoldersAsync(accountId, CancellationToken.None);

        Assert.Empty(folders);
    }

    [Fact]
    public async Task SyncAll_MissingCredential_MarksReauthenticationWithoutThrowing()
    {
        await using var db = CreateDb();
        var (accountId, _) = await SeedFolderAsync(db);
        var service = CreateService(db, Options(100));

        await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(MailAccountStatus.NeedsReauthentication,
            (await db.MailAccounts.SingleAsync(x => x.Id == accountId)).Status);
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

    private static MailFolderSyncService CreateService(AppDbContext db, MailSyncOptions options, FakePushNotificationService? push = null) =>
        new(db,
            new MailCredentialResolver(db, new PassthroughProtector()),
            new Infrastructure.Mail.MailConnectionHelper(
                new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)),
                NullLogger<Infrastructure.Mail.MailConnectionHelper>.Instance),
            new FakeFileStorage(),
            options,
            push ?? new FakePushNotificationService(),
            NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
