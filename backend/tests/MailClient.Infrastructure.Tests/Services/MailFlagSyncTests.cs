using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using static MailClient.Infrastructure.Tests.Services.SyncTestSeed;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class MailFlagSyncTests
{
    private static MailSyncOptions Options() => new()
    {
        Enabled = true,
        PollIntervalSeconds = 30,
        FlagSyncIntervalSeconds = 1,
        MaxMessagesPerRun = 10,
        MaxAttachmentBytes = 500,
        MaxMessageAttachmentBytes = 800,
        MaxMessageBytes = 1000
    };

    [Fact]
    public async Task NewMail_WithoutSeenFlag_MapsToUnread()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a")
        });
        await CreateSyncService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var mail = await db.Mails.SingleAsync();
        Assert.False(mail.IsRead);
    }

    [Fact]
    public async Task NewMail_WithSeenFlag_MapsToRead()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a")
        },
        seenUids: new HashSet<uint> { 100 });
        await CreateSyncService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var mail = await db.Mails.SingleAsync();
        Assert.True(mail.IsRead);
    }

    [Fact]
    public async Task SetRead_AddsSeenFlagAndUpdatesLocal()
    {
        await using var db = CreateDb();
        var mailId = await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: false);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>());
        var service = CreateReadService(db, remote);

        var result = await service.SetReadAsync(SeedUserId, mailId, true, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        Assert.True(result.Value!.IsRead);
        Assert.Contains((100u, true), remote.SetSeenCalls);
        Assert.True((await db.Mails.SingleAsync(item => item.Id == mailId)).IsRead);
    }

    [Fact]
    public async Task SetUnread_RemovesSeenFlagAndUpdatesLocal()
    {
        await using var db = CreateDb();
        var mailId = await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: true);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>());
        var service = CreateReadService(db, remote);

        var result = await service.SetReadAsync(SeedUserId, mailId, false, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        Assert.False(result.Value!.IsRead);
        Assert.Contains((100u, false), remote.SetSeenCalls);
        Assert.False((await db.Mails.SingleAsync(item => item.Id == mailId)).IsRead);
    }

    [Fact]
    public async Task SetRead_ImapFailure_LeavesLocalStateUnchanged()
    {
        await using var db = CreateDb();
        var mailId = await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: false);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>())
        {
            SetSeenFailure = (_, _) => new MailConnectionException(MailConnectionFailure.Network, "unreachable")
        };
        var service = CreateReadService(db, remote);

        var result = await service.SetReadAsync(SeedUserId, mailId, true, CancellationToken.None);

        Assert.Equal(ServiceOutcome.ProviderError, result.Outcome);
        Assert.False((await db.Mails.SingleAsync(item => item.Id == mailId)).IsRead);
    }

    [Fact]
    public async Task SetRead_OtherUsersMail_ReturnsNotFoundWithoutRemoteCall()
    {
        await using var db = CreateDb();
        var mailId = await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: false);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>());
        var service = CreateReadService(db, remote);

        var result = await service.SetReadAsync(Guid.NewGuid(), mailId, true, CancellationToken.None);

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
        Assert.Empty(remote.SetSeenCalls);
    }

    [Fact]
    public async Task SetRead_UidValidityMismatch_ReturnsConflictWithoutMutation()
    {
        await using var db = CreateDb();
        var mailId = await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: false);
        var remote = new FakeRemoteMailFolder(9, new Dictionary<uint, Func<MimeMessage>>());
        var service = CreateReadService(db, remote);

        var result = await service.SetReadAsync(SeedUserId, mailId, true, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Conflict, result.Outcome);
        Assert.Empty(remote.SetSeenCalls);
        Assert.False((await db.Mails.SingleAsync(item => item.Id == mailId)).IsRead);
    }

    [Fact]
    public async Task SetRead_AlreadyInDesiredState_SkipsImap()
    {
        await using var db = CreateDb();
        var mailId = await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: true);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>());
        var service = CreateReadService(db, remote);

        var result = await service.SetReadAsync(SeedUserId, mailId, true, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        Assert.Empty(remote.SetSeenCalls);
    }

    [Fact]
    public async Task SetRead_Cancelled_Propagates()
    {
        await using var db = CreateDb();
        var mailId = await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: false);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>());
        var service = CreateReadService(db, remote);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SetReadAsync(SeedUserId, mailId, true, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Reconcile_AppliesRemoteSeenChangesBothDirections()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: false, folderId: folderId);
        await SeedMailAsync(db, uid: 101, uidValidity: 7, isRead: true, folderId: folderId);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a"),
            [101] = () => SimpleMessage("b")
        },
        seenUids: new HashSet<uint> { 100 });
        var service = CreateSyncService(db);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var mails = await db.Mails.OrderBy(item => item.Uid).ToListAsync();
        Assert.True(mails[0].IsRead);
        Assert.False(mails[1].IsRead);
        Assert.NotNull((await db.SyncStates.SingleAsync()).LastFlagSyncAt);
    }

    [Fact]
    public async Task Reconcile_DoesNotDownloadMessageBodies()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: false, folderId: folderId);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a")
        });

        await CreateSyncService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(0, remote.MessageCallsFor(100));
        Assert.Equal(1, remote.GetFlagsCalls);
    }

    [Fact]
    public async Task Reconcile_MissingRemoteUid_LeavesLocalRowUnchanged()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: true, folderId: folderId);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>());

        await CreateSyncService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.True((await db.Mails.SingleAsync()).IsRead);
        Assert.NotNull((await db.SyncStates.SingleAsync()).LastFlagSyncAt);
    }

    [Fact]
    public async Task Reconcile_Failure_KeepsCheckpointButNotFlagTimestamp()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        await SeedMailAsync(db, uid: 100, uidValidity: 7, isRead: false, folderId: folderId);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("a")
        },
        seenUids: new HashSet<uint> { 100 })
        {
            GetFlagsFailure = () => new InvalidOperationException("flags unavailable")
        };

        await CreateSyncService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var state = await db.SyncStates.SingleAsync();
        Assert.Null(state.LastFlagSyncAt);
        Assert.Equal(100u, state.LastUid);
        Assert.False((await db.Mails.SingleAsync()).IsRead);
    }

    [Fact]
    public async Task Reconcile_SkippedWhenIntervalHasNotElapsed()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderAsync(db);
        var state = await db.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
        state.LastFlagSyncAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>());

        await CreateSyncService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal(0, remote.GetFlagsCalls);
    }

    private static readonly Guid SeedUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task<Guid> SeedMailAsync(
        AppDbContext db,
        uint uid,
        uint uidValidity,
        bool isRead,
        Guid? folderId = null)
    {
        Guid accountId;
        Guid targetFolderId;
        if (folderId is null)
        {
            accountId = Guid.NewGuid();
            targetFolderId = Guid.NewGuid();
            db.Users.Add(new User
            {
                Id = SeedUserId,
                Email = "flags@example.test",
                PasswordHash = "seed",
                DisplayName = "Flags"
            });
            db.MailAccounts.Add(new MailAccount
            {
                Id = accountId,
                UserId = SeedUserId,
                EmailAddress = "flags@example.test",
                DisplayName = "F",
                Username = "f",
                EncryptedPassword = "x",
                ImapHost = "imap.example.test",
                ImapPort = 993,
                SmtpHost = "smtp.example.test",
                SmtpPort = 587
            });
            db.MailFolders.Add(new MailFolder
            {
                Id = targetFolderId,
                MailAccountId = accountId,
                Name = "INBOX",
                FullName = "INBOX",
                IsSyncEnabled = true
            });
            db.SyncStates.Add(new SyncState
            {
                Id = Guid.NewGuid(),
                MailFolderId = targetFolderId,
                UidValidity = uidValidity,
                LastUid = uid
            });
        }
        else
        {
            targetFolderId = folderId.Value;
            accountId = (await db.MailFolders.SingleAsync(item => item.Id == targetFolderId)).MailAccountId;
        }

        var mailId = Guid.NewGuid();
        db.Mails.Add(new Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = targetFolderId,
            Uid = uid,
            UidValidity = uidValidity,
            MessageId = $"flag-{uid}@example.test",
            Subject = "flag",
            FromAddress = "sender@example.test",
            FromDisplayName = "Sender",
            ToAddress = "flags@example.test",
            BodyText = "hello",
            ReceivedAt = DateTime.UtcNow,
            IsRead = isRead
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return mailId;
    }

    private static MailFolderSyncService CreateSyncService(AppDbContext db) =>
        new(db,
            new PassthroughProtector(),
            null!,
            new FakeFileStorage(),
            Options(),
            NullLogger<MailFolderSyncService>.Instance);

    private static MailReadService CreateReadService(AppDbContext db, FakeRemoteMailFolder remote) =>
        new(db,
            new FakeMailFolderClient(remote),
            NullLogger<MailReadService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
