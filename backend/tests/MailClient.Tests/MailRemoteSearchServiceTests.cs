using MailKit;
using MailClient.Infrastructure.Runtime;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Sync;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static MailClient.Tests.SyncTestSeed;

namespace MailClient.Tests;

public sealed class MailRemoteSearchServiceTests
{
    [Fact]
    public async Task SearchCore_ImportsOnlyNewestTwentyFiveMatches_AndReportsRemaining()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, Enumerable.Range(1, 30)
            .ToDictionary(uid => (uint)uid, uid => (Func<MimeKit.MimeMessage>)(() => SimpleMessage($"message-{uid}"))));
        var service = CreateService(db);
        var progress = new MailRemoteSearchService.SearchProgress();
        var folders = await db.MailFolders.AsNoTracking().Where(folder => folder.Id == folderId).ToListAsync();

        await service.SearchAndImportCoreAsync(accountId, folders, SearchQuery.MessageContains("match"), progress,
            (_, _) => Task.FromResult<MailClient.Infrastructure.Email.IRemoteMailFolder>(remote), CancellationToken.None);

        Assert.Equal(30, progress.Matched);
        Assert.Equal(25, progress.Imported);
        Assert.False(progress.Complete);
        Assert.Equal(5, progress.Matched - progress.Imported);
        Assert.Equal(25, await db.Mails.CountAsync());
        Assert.Equal(6u, (await db.Mails.Select(mail => mail.Uid).MinAsync()));
    }

    [Fact]
    public async Task SearchCore_ExcludesAlreadyImportedAndSkippedUidsFromMatched()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new()
        {
            [1] = () => SimpleMessage("already imported"),
            [2] = () => SimpleMessage("skipped"),
            [3] = () => SimpleMessage("new")
        });
        await SyncAsync(db, accountId, folderId, remote, [new UniqueId(1)]);
        db.SyncSkippedUids.Add(new SyncSkippedUid { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, Uid = 2, Reason = "test" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);
        var progress = new MailRemoteSearchService.SearchProgress();
        var folders = await db.MailFolders.AsNoTracking().Where(folder => folder.Id == folderId).ToListAsync();

        await service.SearchAndImportCoreAsync(accountId, folders, SearchQuery.All, progress,
            (_, _) => Task.FromResult<MailClient.Infrastructure.Email.IRemoteMailFolder>(remote), CancellationToken.None);

        Assert.Equal(1, progress.Matched);
        Assert.Equal(1, progress.Imported);
        Assert.True(progress.Complete);
    }

    [Fact]
    public async Task SearchCore_ContendedAccountLockUntilDeadline_DoesNotImport()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new() { [1] = () => SimpleMessage("match") });
        var service = CreateService(db, new AlwaysContendedLockProvider());
        var progress = new MailRemoteSearchService.SearchProgress();
        var folders = await db.MailFolders.AsNoTracking().Where(folder => folder.Id == folderId).ToListAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await service.SearchAndImportCoreAsync(accountId, folders, SearchQuery.All, progress,
            (_, _) => Task.FromResult<MailClient.Infrastructure.Email.IRemoteMailFolder>(remote), deadline.Token);

        Assert.Equal(1, progress.Matched);
        Assert.Equal(0, progress.Imported);
        Assert.False(progress.Complete);
        Assert.Empty(await db.Mails.ToListAsync());
    }

    private sealed class AlwaysContendedLockProvider : ISyncLockProvider
    {
        public Task<SyncLockAcquisition> TryAcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken) =>
            Task.FromResult(SyncLockAcquisition.Contended);
    }

    [Fact]
    public async Task SearchAsync_LabelFilterReturnsCompleteWithoutOpeningImap()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.SearchAsync(Guid.NewGuid(), new MailSearchRequest("text", null, null, null, null, null, null, null, null, null, 1, 0, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(new RemoteSearchResponse(0, 0, 0, true), result);
    }

    [Fact]
    public async Task SearchAsync_FolderOwnedByAnotherAccountReturnsNotFound()
    {
        await using var db = CreateDb();
        var (ownerId, folderId) = await SeedFolderAsync(db);
        var service = CreateService(db);

        var result = await service.SearchAsync(Guid.NewGuid(), new MailSearchRequest("text", folderId, null, null, null, null, null, null, null, null, 1, 0), CancellationToken.None);
        var withLabel = await service.SearchAsync(Guid.NewGuid(), new MailSearchRequest("text", folderId, null, null, null, null, null, null, null, null, 1, 0, Guid.NewGuid()), CancellationToken.None);
        Assert.Null(withLabel);

        Assert.NotEqual(Guid.Empty, ownerId);
        Assert.Null(result);
    }

    private static async Task SyncAsync(AppDbContext db, Guid accountId, Guid folderId, FakeRemoteMailFolder remote, IReadOnlyList<MailKit.UniqueId> uids)
    {
        var runtime = FixedRuntimeSettingsStore.Operation(TestServices.SyncSettings(maxMessagesPerRun: 100));
        var sync = CreateSyncService(db, runtime);
        await sync.ImportRemoteMatchesAsync(accountId, folderId, remote, uids, CancellationToken.None);
    }

    private static MailRemoteSearchService CreateService(AppDbContext db, ISyncLockProvider? locks = null)
    {
        var runtime = FixedRuntimeSettingsStore.Operation(TestServices.SyncSettings(maxMessagesPerRun: 100));
        var sync = CreateSyncService(db, runtime);
        return new MailRemoteSearchService(db, TestServices.Credentials(db),
            new MailConnectionHelper(new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)), NullLogger<MailConnectionHelper>.Instance),
            sync, runtime, locks ?? new InMemorySyncLockProvider(), TimeSpan.FromMilliseconds(100));
    }

    private static MailFolderSyncService CreateSyncService(AppDbContext db, RuntimeOperationSettings runtime) => new(
        db,
        TestServices.Credentials(db),
        new MailConnectionHelper(new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)), NullLogger<MailConnectionHelper>.Instance),
        new FakeFileStorage(),
        runtime,
        new FakePushNotificationService(),
        new ConversationService(db), new MailReconciliationService(db), NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
