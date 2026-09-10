using MailClient.Application.Interfaces;
using MailClient.Application.Sync;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
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

    private static MailFolderSyncService CreateService(AppDbContext db, MailSyncOptions options) =>
        new(db,
            new PassthroughProtector(),
            null!,
            new FakeFileStorage(),
            options,
            NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
