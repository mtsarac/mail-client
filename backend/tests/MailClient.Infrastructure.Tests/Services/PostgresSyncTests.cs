using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Testcontainers.PostgreSql;

namespace MailClient.Infrastructure.Tests.Services;

[CollectionDefinition("postgres-sync")]
public sealed class PostgresSyncCollection : ICollectionFixture<PostgresSyncFixture>;

public sealed class PostgresSyncFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("mailclient_synctests")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}

[Collection("postgres-sync")]
public sealed class PostgresSyncTests(PostgresSyncFixture fixture)
{
    private static MailSyncOptions Options() => new()
    {
        Enabled = true,
        PollIntervalSeconds = 30,
        MaxMessagesPerRun = 10,
        MaxAttachmentBytes = 500,
        MaxMessageAttachmentBytes = 800,
        MaxMessageBytes = 1000
    };

    [Fact]
    public async Task Checkpoint_PersistsAcrossContexts()
    {
        Guid accountId;
        Guid folderId;
        await using (var seed = fixture.CreateDb())
            (accountId, folderId) = await SyncTestSeed.SeedFolderAsync(seed);

        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SyncTestSeed.SimpleMessage("a"),
            [101] = () => SyncTestSeed.SimpleMessage("b"),
            [102] = () => SyncTestSeed.SimpleMessage("c")
        });
        await using (var db = fixture.CreateDb())
            await CreateService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        await using var check = fixture.CreateDb();
        Assert.Equal(3, await check.Mails.CountAsync(m => m.MailFolderId == folderId));
        Assert.Equal(102u, (await check.SyncStates.SingleAsync(s => s.MailFolderId == folderId)).LastUid);
    }

    [Fact]
    public async Task MailAndCheckpoint_AreAtomic()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        Guid accountId;
        Guid folderId;
        await using (var seed = new AppDbContext(options))
            (accountId, folderId) = await SyncTestSeed.SeedFolderAsync(seed);

        await using var db = new FailNextSaveDbContext(options) { FailNext = true };
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SyncTestSeed.SimpleMessage("a")
        });

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            CreateService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None));

        await using var check = new AppDbContext(options);
        Assert.Empty(await check.Mails.Where(m => m.MailFolderId == folderId).ToListAsync());
        Assert.Empty(await check.SyncSkippedUids.Where(s => s.MailFolderId == folderId).ToListAsync());
        Assert.Equal(0u, (await check.SyncStates.SingleAsync(s => s.MailFolderId == folderId)).LastUid);
    }

    [Fact]
    public async Task UpsertAsync_BoundedSelects()
    {
        Guid accountId;
        await using (var seed = fixture.CreateDb())
            (accountId, _) = await SyncTestSeed.SeedFolderAsync(seed);

        var counter = new SelectCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        await using var db = new AppDbContext(options);
        var folders = await new MailFolderService(
                db, new PassthroughProtector(), new FixedExplorer(), NullLogger<MailFolderService>.Instance)
            .UpsertAsync(accountId,
            [
                new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 7, true),
                new DiscoveredMailFolder("Sent", "Sent", MailFolderType.Sent, 7, true),
                new DiscoveredMailFolder("A", "A", MailFolderType.Custom, 1, false),
                new DiscoveredMailFolder("B", "B", MailFolderType.Custom, 1, false),
                new DiscoveredMailFolder("C", "C", MailFolderType.Custom, 1, false)
            ], CancellationToken.None);

        Assert.Equal(5, folders.Count);
        Assert.True(counter.Selects <= 3);
    }

    [Fact]
    public async Task DuplicateMail_RejectedByUniqueIndex()
    {
        await using var db = fixture.CreateDb();
        var (accountId, folderId) = await SyncTestSeed.SeedFolderAsync(db);
        db.Mails.Add(new Mail
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            MailFolderId = folderId,
            Uid = 100,
            UidValidity = 7,
            Subject = "first",
            ReceivedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.Mails.Add(new Mail
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            MailFolderId = folderId,
            Uid = 100,
            UidValidity = 7,
            Subject = "duplicate",
            ReceivedAt = DateTime.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DuplicateSkippedUid_RejectedByUniqueIndex()
    {
        await using var db = fixture.CreateDb();
        var (_, folderId) = await SyncTestSeed.SeedFolderAsync(db);
        db.SyncSkippedUids.Add(new SyncSkippedUid
        {
            Id = Guid.NewGuid(),
            MailFolderId = folderId,
            Uid = 100,
            Reason = "failed: boom",
            SkippedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.SyncSkippedUids.Add(new SyncSkippedUid
        {
            Id = Guid.NewGuid(),
            MailFolderId = folderId,
            Uid = 100,
            Reason = "failed: again",
            SkippedAt = DateTime.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task UidValidityReset_ClearsFolderData()
    {
        await using var db = fixture.CreateDb();
        var (accountId, folderId) = await SyncTestSeed.SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SyncTestSeed.SimpleMessage("old")
        });
        var service = CreateService(db);
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        Assert.Single(await db.Mails.Where(m => m.MailFolderId == folderId).ToListAsync());

        var reset = new FakeRemoteMailFolder(9, new Dictionary<uint, Func<MimeMessage>>
        {
            [1] = () => SyncTestSeed.SimpleMessage("new")
        });
        await service.SyncFolderCoreAsync(accountId, folderId, reset, CancellationToken.None);

        var mails = await db.Mails.Where(m => m.MailFolderId == folderId).ToListAsync();
        Assert.Single(mails);
        Assert.Equal(1u, mails[0].Uid);
        Assert.Equal(1u, (await db.SyncStates.SingleAsync(s => s.MailFolderId == folderId)).LastUid);
    }

    [Fact]
    public async Task ConcurrentLockAcquisition_BlocksSecondAcquirer()
    {
        await using var holder = fixture.CreateDb();
        var folderId = Guid.NewGuid();
        await using var folderLock = await FolderAdvisoryLock.AcquireAsync(holder, folderId, CancellationToken.None);

        await using var waiter = fixture.CreateDb();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FolderAdvisoryLock.AcquireAsync(waiter, folderId, timeout.Token));

        await folderLock.DisposeAsync();
        await using var acquired = await FolderAdvisoryLock.AcquireAsync(waiter, folderId, CancellationToken.None);
    }

    [Fact]
    public async Task DeleteAsync_WaitsForHeldFolderLock()
    {
        await using var seed = fixture.CreateDb();
        var userId = Guid.NewGuid();
        seed.Users.Add(new User
        {
            Id = userId,
            Email = "u@example.test",
            PasswordHash = "x",
            DisplayName = "U"
        });
        await seed.SaveChangesAsync();
        var storage = new FakeFileStorage();
        var created = await CreateAccountService(seed, storage)
            .CreateAsync(userId, AccountRequest(), CancellationToken.None);
        var folderId = Guid.NewGuid();
        seed.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
        {
            Id = folderId,
            MailAccountId = created.Id,
            Name = "INBOX",
            FullName = "INBOX",
            IsSyncEnabled = true
        });
        await seed.SaveChangesAsync();

        await using var holder = fixture.CreateDb();
        await using var folderLock = await FolderAdvisoryLock.AcquireAsync(holder, folderId, CancellationToken.None);

        await using var deleter = fixture.CreateDb();
        var deleteTask = CreateAccountService(deleter, storage)
            .DeleteAsync(userId, created.Id, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Deletion holds no folder lock yet: it must still be waiting on the sync lock.
        Assert.False(deleteTask.IsCompleted);

        await folderLock.DisposeAsync();
        Assert.True(await deleteTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(created.Id, storage.DeletedAccounts);
        await using var check = fixture.CreateDb();
        Assert.Empty(await check.MailAccounts.Where(a => a.Id == created.Id).ToListAsync());
    }

    [Fact]
    public async Task SeenFlag_PersistsAsRead_OnPostgres()
    {
        Guid accountId;
        Guid folderId;
        await using (var seed = fixture.CreateDb())
            (accountId, folderId) = await SyncTestSeed.SeedFolderAsync(seed);

        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SyncTestSeed.SimpleMessage("seen")
        },
        seenUids: new HashSet<uint> { 100 });
        await using (var db = fixture.CreateDb())
            await CreateService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        await using var check = fixture.CreateDb();
        Assert.True(await check.Mails.Where(m => m.MailFolderId == folderId).Select(m => m.IsRead).SingleAsync());
    }

    [Fact]
    public async Task FlagReconciliation_FlipsReadState_OnPostgres()
    {
        Guid accountId;
        Guid folderId;
        await using (var seed = fixture.CreateDb())
            (accountId, folderId) = await SyncTestSeed.SeedFolderAsync(seed);

        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SyncTestSeed.SimpleMessage("a")
        });
        await using (var db = fixture.CreateDb())
            await CreateService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        await using (var db = fixture.CreateDb())
        {
            var state = await db.SyncStates.SingleAsync(s => s.MailFolderId == folderId);
            state.LastFlagSyncAt = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var changed = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SyncTestSeed.SimpleMessage("a"),
            [101] = () => SyncTestSeed.SimpleMessage("b")
        },
        seenUids: new HashSet<uint> { 100 });
        await using (var db = fixture.CreateDb())
            await CreateService(db).SyncFolderCoreAsync(accountId, folderId, changed, CancellationToken.None);

        await using var check = fixture.CreateDb();
        var mails = await check.Mails
            .Where(m => m.MailFolderId == folderId)
            .OrderBy(m => m.Uid)
            .Select(m => m.IsRead)
            .ToListAsync();
        Assert.Equal([true, false], mails);
        Assert.NotNull(await check.SyncStates
            .Where(s => s.MailFolderId == folderId)
            .Select(s => s.LastFlagSyncAt)
            .SingleAsync());
    }

    private static MailFolderSyncService CreateService(AppDbContext db) => new(
        db,
        new PassthroughProtector(),
        null!,
        new FakeFileStorage(),
        Options(),
        new FakePushNotificationService(),
        NullLogger<MailFolderSyncService>.Instance);

    private static MailAccountService CreateAccountService(AppDbContext db, IFileStorage storage) => new(
        db,
        new PassthroughProtector(),
        new TestEnvironment(),
        new AllowConnectivityTester(),
        new AllowHostValidator(),
        storage,
        NullLogger<MailAccountService>.Instance);

    private static MailAccountRequest AccountRequest() => new(
        "account@example.com", "Account", "account@example.com", "password",
        "imap.example.com", 993, MailSecurity.SslOnConnect,
        "smtp.example.com", 587, MailSecurity.StartTls, true);

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MailClient.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AllowConnectivityTester : IMailConnectivityTester
    {
        public Task TestImapAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task TestSmtpAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class AllowHostValidator : IOutboundHostValidator
    {
        public HostCheckResult CheckLiteralHost(string? host) => HostCheckResult.Allow();
        public Task<HostCheckResult> CheckAsync(string? host, CancellationToken cancellationToken) =>
            Task.FromResult(HostCheckResult.Allow());
        public Task<ValidatedHost> ResolveAllowedAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new ValidatedHost(host, System.Net.IPAddress.Parse("93.184.216.34")));
    }

    private sealed class FixedExplorer : IMailFolderExplorer
    {
        public Task<IReadOnlyList<DiscoveredMailFolder>> ExploreAsync(
            MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveredMailFolder>>([]);
    }

    private sealed class SelectCounter : DbCommandInterceptor
    {
        public int Selects;
        public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                Selects++;
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
