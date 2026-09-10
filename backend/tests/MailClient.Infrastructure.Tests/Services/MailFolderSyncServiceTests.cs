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
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b"),
            [102] = () => SimpleMessage("c")
        });
        var service = CreateService(db, storage: new FakeStorage());

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
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        });
        await CreateService(db, storage: new FakeStorage()).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        remote.Messages[102] = () => SimpleMessage("c");
        await CreateService(db, storage: new FakeStorage()).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

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
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a")
        });
        var service = CreateService(db, storage: new FakeStorage());
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
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        });
        await CreateService(db, storage: new FakeStorage()).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        await using var restarted = CreateDb(dbName);
        Assert.Equal(101u, (await restarted.SyncStates.SingleAsync()).LastUid);
        remote.Messages[102] = () => SimpleMessage("c");
        await CreateService(restarted, storage: new FakeStorage())
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(3, await restarted.Mails.CountAsync());
        Assert.Equal(102u, (await restarted.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task UidValidityReset_ClearsMailsSkipsAndAttachments()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var storage = new FakeStorage();
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithAttachment("old")
        });
        await CreateService(db, storage: storage).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        Assert.Single(await db.Mails.ToListAsync());

        var reset = new FakeRemote(9, new Dictionary<uint, Func<MimeMessage>>
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
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithAttachment("report.txt")
        });
        var storage = new FakeStorage();
        await CreateService(db, storage: storage).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var attachment = await db.Attachments.SingleAsync();
        Assert.Equal("report.txt", attachment.FileName);
        Assert.NotEmpty(attachment.StoragePath);
        Assert.Contains(storage.Saved, path => path == attachment.StoragePath);
    }

    [Fact]
    public async Task AttachmentSizeRejection_SavesMailWithoutAttachment()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithAttachment("big.bin")
        });
        var storage = new FakeStorage(rejectFileNames: ["big.bin"]);
        await CreateService(db, storage: storage).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Empty(await db.Attachments.ToListAsync());
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task StorageFailure_CleansCreatedFilesAndSkipsPoison()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => MessageWithAttachment("boom.bin"),
            [101] = () => SimpleMessage("next")
        });
        var storage = new FakeStorage(failFileNames: ["boom.bin"]);
        await CreateService(db, storage: storage).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        // UID 100 poisoned by storage failure: later UID still synced, checkpoint at final UID.
        Assert.Single(await db.Mails.ToListAsync());
        Assert.Equal(101u, (await db.SyncStates.SingleAsync()).LastUid);
        Assert.Single(await db.SyncSkippedUids.ToListAsync());
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

        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a")
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(db, storage: new FakeStorage()).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None));

        await using var check = new AppDbContext(options);
        Assert.Equal(0u, (await check.SyncStates.SingleAsync()).LastUid);
        Assert.Empty(await check.Mails.ToListAsync());
    }

    [Fact]
    public async Task PoisonMessage_DoesNotBlockLaterUidsAndIsNotRetried()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("ok-100"),
            [101] = () => throw new FormatException("bad mime"),
            [102] = () => SimpleMessage("ok-102"),
            [103] = () => SimpleMessage("ok-103")
        });
        var service = CreateService(db, storage: new FakeStorage());
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
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(db, storage: new FakeStorage()).SyncFolderCoreAsync(accountId, folderId, remote, cancelled.Token));

        Assert.Empty(await db.Mails.ToListAsync());
    }

    [Fact]
    public async Task MaxMessagesPerRun_BoundsSingleRun()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b"),
            [102] = () => SimpleMessage("c")
        });
        var service = CreateService(db, options: Options(maxMessages: 2), storage: new FakeStorage());
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
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("sized")
        }, sizes: new Dictionary<uint, uint> { [100] = (uint)size });
        await CreateService(db, options: Options(maxMessageBytes: 1000), storage: new FakeStorage())
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
        var remote = new FakeRemote(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("huge"),
            [101] = () => SimpleMessage("fine")
        }, sizes: new Dictionary<uint, uint> { [100] = 5000, [101] = 10 });
        await CreateService(db, options: Options(maxMessageBytes: 1000), storage: new FakeStorage())
            .SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Equal(0, remote.MessageCallsFor(100));
        Assert.Equal(1, remote.MessageCallsFor(101));
        Assert.Equal(101u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    private static MailFolderSyncService CreateService(
        AppDbContext db,
        MailSyncOptions? options = null,
        IFileStorage? storage = null) =>
        new(db,
            new PassthroughProtector(),
            null!,
            storage ?? new FakeStorage(),
            options ?? Options(),
            NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext CreateDb(string? name = null) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options);

    private static async Task<(Guid AccountId, Guid FolderId)> SeedFolderAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailClient.Domain.Entities.MailAccount
        {
            Id = accountId,
            UserId = Guid.NewGuid(),
            EmailAddress = "a@example.test",
            DisplayName = "A",
            Username = "a",
            EncryptedPassword = "x",
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
        db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailFolderId = folderId, UidValidity = 7, LastUid = 0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId);
    }

    private static MimeMessage SimpleMessage(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Receiver", "receiver@example.test"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "hello" };
        return message;
    }

    private static MimeMessage MessageWithAttachment(string fileName)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Receiver", "receiver@example.test"));
        message.Subject = "with attachment";
        var body = new BodyBuilder { TextBody = "see attached" };
        body.Attachments.Add(fileName, [1, 2, 3]);
        message.Body = body.ToMessageBody();
        return message;
    }

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class FakeStorage(
        string[]? rejectFileNames = null,
        string[]? failFileNames = null) : IFileStorage
    {
        public List<string> Saved { get; } = [];
        public List<string> Deleted { get; } = [];

        public Task<StoredFile> SaveAsync(Guid accountId, Guid mailId, Guid attachmentId,
            Func<Stream, CancellationToken, Task> write, long maxBytes, CancellationToken cancellationToken)
        {
            var fileName = CurrentFileName();
            if (failFileNames?.Contains(fileName) == true)
                throw new IOException("disk failed");
            if (rejectFileNames?.Contains(fileName) == true)
                throw new AttachmentLimitExceededException();
            var path = $"fake/{Guid.NewGuid():N}";
            Saved.Add(path);
            return Task.FromResult(new StoredFile(path, 3));
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
        {
            Deleted.Add(relativePath);
            return Task.CompletedTask;
        }

        private static string? CurrentFileName() => TestAttachmentName.Current?.Value;

        public static class TestAttachmentName
        {
            public static AsyncLocal<string?> Current { get; } = new();
        }
    }

    private sealed class FakeRemote(uint uidValidity, Dictionary<uint, Func<MimeMessage>> messages, Dictionary<uint, uint>? sizes = null)
        : IRemoteMailFolder
    {
        public Dictionary<uint, Func<MimeMessage>> Messages { get; } = messages;
        public Dictionary<uint, int> GetMessageCalls { get; } = [];
        public uint UidValidity { get; } = uidValidity;
        private readonly Dictionary<uint, uint> _sizes = sizes ?? [];

        public int MessageCallsFor(uint uid) => GetMessageCalls.GetValueOrDefault(uid, 0);

        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IList<UniqueId>> SearchNewAsync(uint afterUid, CancellationToken cancellationToken) =>
            Task.FromResult<IList<UniqueId>>(Messages.Keys
                .Where(uid => uid > afterUid)
                .OrderBy(uid => uid)
                .Select(uid => new UniqueId(uid))
                .ToList());

        public Task<uint?> GetSizeAsync(UniqueId uid, CancellationToken cancellationToken) =>
            Task.FromResult<uint?>(_sizes.GetValueOrDefault(uid.Id, 100u));

        public Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken)
        {
            GetMessageCalls[uid.Id] = MessageCallsFor(uid.Id) + 1;
            var message = Messages[uid.Id]();
            foreach (var part in message.BodyParts.OfType<MimePart>())
                FakeStorage.TestAttachmentName.Current.Value = part.FileName;
            return Task.FromResult(message);
        }
    }

    private sealed class AlwaysFailingDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<int>(new InvalidOperationException("database unavailable"));
    }
}
