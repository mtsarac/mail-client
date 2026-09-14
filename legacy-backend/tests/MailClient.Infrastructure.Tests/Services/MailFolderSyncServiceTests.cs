using MailClient.Application.Interfaces;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using System.Net.Sockets;
using static MailClient.Infrastructure.Tests.Services.SyncTestSeed;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class MailFolderSyncServiceTests
{
    private static MailSyncOptions Options(int maxMessages = 10, long maxMessageBytes = 1000) => new()
    {
        Enabled = true,
        PollIntervalSeconds = 30,
        MaxMessagesPerRun = maxMessages,
        MaxAttachmentBytes = 500,
        MaxMessageAttachmentBytes = 800,
        MaxMessageBytes = maxMessageBytes
    };

    [Fact]
    public async Task FirstSync_InsertsMailsAndAdvancesCheckpointToFinalUid()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b"),
            [102] = () => SimpleMessage("c")
        });
        var service = CreateService(db, storage: new FakeFileStorage());

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(3, await db.Mails.CountAsync());
        Assert.Equal(102u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task IncrementalSync_ProcessesOnlyNewUids()
    {
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDb(dbName);
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        });
        await CreateService(db, storage: new FakeFileStorage()).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        remote.Messages[102] = () => SimpleMessage("c");
        await CreateService(db, storage: new FakeFileStorage()).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(3, await db.Mails.CountAsync());
        Assert.Equal(102u, (await db.SyncStates.SingleAsync()).LastUid);
        Assert.Equal(1, remote.MessageCallsFor(100));
        Assert.Equal(1, remote.MessageCallsFor(101));
        Assert.Equal(1, remote.MessageCallsFor(102));
    }

    [Fact]
    public async Task NoNewMessages_DoesNotDuplicate()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a")
        });
        var service = CreateService(db, storage: new FakeFileStorage());
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task RetryAfterRestart_ContinuesFromPersistedCheckpoint()
    {
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDb(dbName);
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        });
        await CreateService(db, storage: new FakeFileStorage()).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        await using var restarted = CreateDb(dbName);
        Assert.Equal(101u, (await restarted.SyncStates.SingleAsync()).LastUid);
        remote.Messages[102] = () => SimpleMessage("c");
        await CreateService(restarted, storage: new FakeFileStorage())
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(3, await restarted.Mails.CountAsync());
        Assert.Equal(102u, (await restarted.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task UidValidityReset_ClearsMailsSkipsAndAttachments()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var storage = new FakeFileStorage();
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithAttachment("old")
        });
        await CreateService(db, storage: storage).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        Assert.Single(await db.Mails.ToListAsync());

        var reset = new FakeRemoteMailFolder(9, new Dictionary<uint, Func<MimeMessage>>
        {
            [1] = () => SimpleMessage("new")
        });
        await CreateService(db, storage: storage).SyncFolderCoreAsync(accountId, folderId, reset, CancellationToken.None);

        var mails = await db.Mails.ToListAsync();
        Assert.Single(mails);
        Assert.Equal(1u, mails[0].Uid);
        Assert.Equal(1u, (await db.SyncStates.SingleAsync()).LastUid);
        Assert.Single(storage.Deleted);
    }

    [Fact]
    public async Task Attachment_PersistedWithStoragePath()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithAttachment("report.txt")
        });
        var storage = new FakeFileStorage();
        await CreateService(db, storage: storage).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var attachment = await db.Attachments.SingleAsync();
        Assert.Equal("report.txt", attachment.FileName);
        Assert.NotEmpty(attachment.StoragePath);
        Assert.Contains(storage.Saved, path => path == attachment.StoragePath);
        Assert.True((await db.Mails.SingleAsync()).HasAttachments);
    }

    [Fact]
    public async Task AttachmentSizeRejection_SavesMailWithoutAttachment()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithAttachment("big.bin")
        });
        var storage = new FakeFileStorage(rejectFileNames: ["big.bin"]);
        await CreateService(db, storage: storage).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Empty(await db.Attachments.ToListAsync());
        Assert.False((await db.Mails.SingleAsync()).HasAttachments);
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task MixedAttachments_FlagReflectsStoredOnly()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithTwoAttachments("keep.txt", "drop.bin")
        });
        await CreateService(db, storage: new FakeFileStorage(rejectFileNames: ["drop.bin"]))
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.True((await db.Mails.SingleAsync()).HasAttachments);
        var attachments = await db.Attachments.ToListAsync();
        Assert.Single(attachments);
        Assert.Equal("keep.txt", attachments[0].FileName);
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task TransientStorageFailure_AbortsRunWithoutSkip()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithAttachment("boom.bin"),
            [101] = () => SimpleMessage("next")
        });
        var service = CreateService(db, storage: new FakeFileStorage(failFileNames: ["boom.bin"]));

        await Assert.ThrowsAsync<IOException>(() =>
            service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None));

        Assert.Empty(await db.Mails.ToListAsync());
        Assert.Empty(await db.SyncSkippedUids.ToListAsync());
        Assert.Equal(0u, (await db.SyncStates.SingleAsync()).LastUid);

        await CreateService(db, storage: new FakeFileStorage())
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(2, await db.Mails.CountAsync());
        Assert.Equal(101u, (await db.SyncStates.SingleAsync()).LastUid);
        Assert.Empty(await db.SyncSkippedUids.ToListAsync());
    }

    [Fact]
    public async Task TransientDbFailure_DoesNotSkipOrAdvance()
    {
        var options = CreateOptions();
        Guid accountId;
        Guid folderId;
        await using (var seed = new AppDbContext(options))
            (accountId, folderId) = await SeedFolderAsync(seed);

        await using var db = new FailNextSaveDbContext(options) { FailNext = true };
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        });

        // The mail commit fails but the skip commit would succeed: the UID must
        // still not be recorded as skipped.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            CreateService(db, storage: new FakeFileStorage())
                .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None));

        await using var check = new AppDbContext(options);
        Assert.Empty(await check.Mails.ToListAsync());
        Assert.Empty(await check.SyncSkippedUids.ToListAsync());
        Assert.Equal(0u, (await check.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task TransientImapFailure_RetriesWithoutSkip()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        });
        remote.Failures[100] = () => new SocketException(10060);
        var service = CreateService(db, storage: new FakeFileStorage());

        await Assert.ThrowsAsync<SocketException>(() =>
            service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None));

        Assert.Empty(await db.Mails.ToListAsync());
        Assert.Empty(await db.SyncSkippedUids.ToListAsync());
        Assert.Equal(0u, (await db.SyncStates.SingleAsync()).LastUid);

        remote.Failures.Clear();
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(2, await db.Mails.CountAsync());
        Assert.Equal(101u, (await db.SyncStates.SingleAsync()).LastUid);
        Assert.Empty(await db.SyncSkippedUids.ToListAsync());
    }

    [Fact]
    public async Task MissingSizeSummary_SkipsAsGoneWithoutDownload()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7,
            new Dictionary<uint, Func<MimeMessage>>
            {
                [100] = () => SimpleMessage("gone"),
                [101] = () => SimpleMessage("here")
            },
            missingSizes: new HashSet<uint> { 100 });
        await CreateService(db, storage: new FakeFileStorage())
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Equal(0, remote.MessageCallsFor(100));
        Assert.Equal(1, remote.MessageCallsFor(101));
        Assert.Equal(101u, (await db.SyncStates.SingleAsync()).LastUid);
        var skip = await db.SyncSkippedUids.SingleAsync();
        Assert.Equal(100u, skip.Uid);
        Assert.StartsWith("gone", skip.Reason);
    }

    [Fact]
    public async Task BatchSizes_FetchedOncePerRun()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b"),
            [102] = () => SimpleMessage("c")
        });
        await CreateService(db, storage: new FakeFileStorage())
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(1, remote.GetSummariesCalls);
        Assert.Equal(3, await db.Mails.CountAsync());
    }

    [Fact]
    public async Task PersistenceFailure_DoesNotAdvanceCheckpoint()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        await using var seed = new AppDbContext(options);
        var ids = await SeedFolderAsync(seed);
        var accountId = ids.AccountId;
        var folderId = ids.FolderId;
        await using var db = new AlwaysFailingDbContext(options);

        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a")
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(db, storage: new FakeFileStorage()).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None));

        await using var check = new AppDbContext(options);
        Assert.Equal(0u, (await check.SyncStates.SingleAsync()).LastUid);
        Assert.Empty(await check.Mails.ToListAsync());
    }

    [Fact]
    public async Task PoisonMessage_DoesNotBlockLaterUidsAndIsNotRetried()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("ok-100"),
            [101] = () => throw new FormatException("bad mime"),
            [102] = () => SimpleMessage("ok-102"),
            [103] = () => SimpleMessage("ok-103")
        });
        var service = CreateService(db, storage: new FakeFileStorage());
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(3, await db.Mails.CountAsync());
        Assert.DoesNotContain(await db.Mails.Select(mail => mail.Uid).ToListAsync(), uid => uid == 101);
        Assert.Equal(103u, (await db.SyncStates.SingleAsync()).LastUid);
        var skip = await db.SyncSkippedUids.SingleAsync();
        Assert.Equal(101u, skip.Uid);
        var callsAfterFirstRun = remote.MessageCallsFor(101);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(callsAfterFirstRun, remote.MessageCallsFor(101));
        Assert.Equal(3, await db.Mails.CountAsync());
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutAdvancingCheckpoint()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(db, storage: new FakeFileStorage()).SyncFolderCoreAsync(accountId, folderId, remote, cancelled.Token));

        Assert.Empty(await db.Mails.ToListAsync());
    }

    [Fact]
    public async Task MaxMessagesPerRun_BoundsSingleRun()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b"),
            [102] = () => SimpleMessage("c")
        });
        var service = CreateService(db, options: Options(maxMessages: 2), storage: new FakeFileStorage());
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(2, await db.Mails.CountAsync());
        Assert.Equal(101u, (await db.SyncStates.SingleAsync()).LastUid);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        Assert.Equal(3, await db.Mails.CountAsync());
        Assert.Equal(102u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Theory]
    [InlineData(999, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public async Task WholeMessageSizeLimit_EnforcedWithoutFullDownload(int size, bool downloaded)
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("sized")
        }, sizes: new Dictionary<uint, uint> { [100] = (uint)size });
        await CreateService(db, options: Options(maxMessageBytes: 1000), storage: new FakeFileStorage())
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(downloaded, remote.MessageCallsFor(100) > 0);
        Assert.Equal(downloaded ? 1 : 0, await db.Mails.CountAsync());
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);
        if (!downloaded)
            Assert.Single(await db.SyncSkippedUids.ToListAsync());
    }

    [Fact]
    public async Task OversizedMessage_DoesNotBlockLaterUids()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("huge"),
            [101] = () => SimpleMessage("fine")
        }, sizes: new Dictionary<uint, uint> { [100] = 5000, [101] = 10 });
        await CreateService(db, options: Options(maxMessageBytes: 1000), storage: new FakeFileStorage())
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Equal(0, remote.MessageCallsFor(100));
        Assert.Equal(1, remote.MessageCallsFor(101));
        Assert.Equal(101u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task SyncAllAsync_UndecryptableCredential_DoesNotThrowOrBlockPoll()
    {
        await using var db = CreateDb();
        var badId = await SeedAccountAsync(db, "bad");
        var goodId = await SeedAccountAsync(db, "good");
        var service = new MailFolderSyncService(db,
            new ThrowingProtector(),
            null!,
            new FakeFileStorage(),
            Options(),
            new FakePushNotificationService(),
            NullLogger<MailFolderSyncService>.Instance);

        await service.SyncAllAsync(CancellationToken.None);

        Assert.Empty(await db.Mails.ToListAsync());
        Assert.NotEqual(badId, goodId);
    }

    private static async Task<Guid> SeedAccountAsync(AppDbContext db, string passwordMarker)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailClient.Domain.Entities.MailAccount
        {
            Id = accountId,
            UserId = Guid.NewGuid(),
            EmailAddress = $"{passwordMarker}@example.test",
            DisplayName = passwordMarker,
            Username = passwordMarker,
            EncryptedPassword = passwordMarker,
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587
        });
        db.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            IsSyncEnabled = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return accountId;
    }

    private static MailFolderSyncService CreateService(
        AppDbContext db,
        MailSyncOptions? options = null,
        IFileStorage? storage = null) =>
        new(db,
            new PassthroughProtector(),
            null!,
            storage ?? new FakeFileStorage(),
            options ?? Options(),
            new FakePushNotificationService(),
            NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext CreateDb(string? name = null) => new(CreateOptions(name));

    private static DbContextOptions<AppDbContext> CreateOptions(string? name = null) => new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options;

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class ThrowingProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) =>
            protectedValue == "bad"
                ? throw new System.Security.Cryptography.CryptographicException("key not found")
                : protectedValue;
    }

    private sealed class AlwaysFailingDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<int>(new InvalidOperationException("database unavailable"));
    }
}
