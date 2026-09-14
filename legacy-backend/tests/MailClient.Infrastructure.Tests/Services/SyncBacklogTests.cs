using MailClient.Application.Interfaces;
using MailClient.Application.Sync;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using static MailClient.Infrastructure.Tests.Services.SyncTestSeed;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class SyncBacklogTests
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
            .ToDictionary(uid => (uint)uid, uid => (Func<MimeMessage>)(() => SimpleMessage($"m{uid}")));
        var remote = new FakeRemoteMailFolder(7, messages);
        var service = CreateService(db, Options(100));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(100, remote.LastSearchMaxCount);
        Assert.Equal(100, await db.Mails.CountAsync());
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);
        Assert.Equal(100u, await db.Mails.MaxAsync(item => item.Uid));
    }

    [Fact]
    public async Task Backlog_ResumesAcrossRuns_WithoutGaps()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var messages = Enumerable.Range(1, 250)
            .ToDictionary(uid => (uint)uid, uid => (Func<MimeMessage>)(() => SimpleMessage($"m{uid}")));
        var remote = new FakeRemoteMailFolder(7, messages);
        var service = CreateService(db, Options(100));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        Assert.Equal(200u, (await db.SyncStates.SingleAsync()).LastUid);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        Assert.Equal(250u, (await db.SyncStates.SingleAsync()).LastUid);

        Assert.Equal(250, await db.Mails.CountAsync());
        Assert.Equal(250, await db.Mails.Select(item => item.Uid).Distinct().CountAsync());
    }

    [Fact]
    public async Task ExtremelyDistantUid_EventuallyDiscovered_WithBoundedPolls()
    {
        const uint distantUid = 100000000;
        const uint remoteUidNext = 100000001;
        var dbName = Guid.NewGuid().ToString("N");
        Guid accountId;
        Guid folderId;
        await using (var seed = CreateDb(dbName))
            (accountId, folderId) = await SeedFolderAsync(seed);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [distantUid] = () => SimpleMessage("distant")
        });
        var existing = new List<uint> { distantUid };
        var polls = 0;
        var maxCallsPerPoll = 0;
        var maxMaterializedPerPoll = 0;
        remote.SearchHook = async (afterUid, maxCount, ct) =>
        {
            var calls = 0;
            var result = await MailKitRemoteMailFolder.SearchPagedAsync(
                afterUid, maxCount, () => remoteUidNext,
                (low, high, pageCt) =>
                {
                    calls++;
                    IList<UniqueId> page = existing
                        .Where(uid => uid >= low && uid <= high)
                        .Select(uid => new UniqueId(uid))
                        .ToList();
                    return Task.FromResult(page);
                }, ct);
            maxCallsPerPoll = Math.Max(maxCallsPerPoll, calls);
            maxMaterializedPerPoll = Math.Max(maxMaterializedPerPoll, result.Uids.Count);
            return (result.Uids, result.ScannedUpTo);
        };

        long cursorAfterFirst = 0;
        long cursorAfterSecond = 0;
        var found = false;
        for (polls = 1; polls <= 20 && !found; polls++)
        {
            await using var db = CreateDb(dbName);
            await CreateService(db, Options(100)).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
            await using var check = CreateDb(dbName);
            found = await check.Mails.AnyAsync(item => item.Uid == distantUid);
            var cursor = (await check.SyncStates.SingleAsync()).NextUidScanStart;
            if (polls == 1)
                cursorAfterFirst = cursor;
            if (polls == 2)
                cursorAfterSecond = cursor;
        }

        Assert.True(found);
        Assert.True(cursorAfterFirst > 1);
        Assert.True(cursorAfterSecond > cursorAfterFirst);
        Assert.True(maxCallsPerPoll <= 8);
        Assert.True(maxMaterializedPerPoll <= 100);
    }

    [Fact]
    public async Task UidValidityReset_ResetsScanCursor()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var state = await db.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
        state.UidValidity = 6;
        state.LastUid = 500;
        state.NextUidScanStart = 999;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
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
    public async Task UpgradedState_LastUid500_Cursor501_ContinuesWithoutRescanOrWrap()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var state = await db.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
        state.UidValidity = 7;
        state.LastUid = 500;
        state.NextUidScanStart = 501;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [320] = () => SimpleMessage("old-below-checkpoint"),
            [501] = () => SimpleMessage("first-after-upgrade"),
            [512] = () => SimpleMessage("later")
        });

        await CreateService(db, Options(100)).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(500u, remote.LastSearchAfterUid);
        Assert.Equal(2, await db.Mails.CountAsync());
        Assert.DoesNotContain(await db.Mails.Select(item => item.Uid).ToListAsync(), uid => uid <= 500);
        var updated = await db.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
        Assert.Equal(512u, updated.LastUid);
        Assert.Equal(513L, updated.NextUidScanStart);
    }

    [Fact]
    public async Task FullyScannedCursor_Sentinel_DoesNotRescanOrWrap()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var state = await db.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
        state.UidValidity = 7;
        state.LastUid = uint.MaxValue;
        state.NextUidScanStart = (long)uint.MaxValue + 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [uint.MaxValue] = () => SimpleMessage("max")
        });

        await CreateService(db, Options(100)).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(uint.MaxValue, remote.LastSearchAfterUid);
        Assert.Empty(await db.Mails.ToListAsync());
        var updated = await db.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
        Assert.Equal(uint.MaxValue, updated.LastUid);
        Assert.Equal((long)uint.MaxValue + 1, updated.NextUidScanStart);
    }

    [Fact]
    public async Task MaxUidValue_ImportsWithoutOverflow_AndRemainsStable()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [uint.MaxValue] = () => SimpleMessage("max")
        });
        var service = CreateService(db, Options(100));

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        var state = await db.SyncStates.SingleAsync();
        Assert.Equal(uint.MaxValue, state.LastUid);
        Assert.Equal((long)uint.MaxValue + 1, state.NextUidScanStart);
    }

    private static MailFolderSyncService CreateService(AppDbContext db, MailSyncOptions options) =>
        new(db,
            new PassthroughProtector(),
            null!,
            new FakeFileStorage(),
            options,
            new FakePushNotificationService(),
            NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static AppDbContext CreateDb(string name) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(name).Options);

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
