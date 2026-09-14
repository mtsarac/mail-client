using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using static MailClient.Infrastructure.Tests.Services.SyncTestSeed;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class SyncPushTests
{
    [Fact]
    public async Task NewInboxMail_NotifiesOnce()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedInboxAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("hello")
        });
        var push = new FakePushNotificationService();
        var service = CreateService(db, push);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var notification = Assert.Single(push.Notifications);
        Assert.Equal((await db.Mails.SingleAsync()).Id, notification.MailId);
        Assert.Equal(accountId, notification.AccountId);
        Assert.Equal(folderId, notification.FolderId);
        Assert.Equal("Sender", notification.Sender);
        Assert.Equal("hello", notification.Subject);
    }

    [Fact]
    public async Task NewSentMail_DoesNotNotify()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedFolderOfTypeAsync(db, MailFolderType.Sent);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("hello")
        });
        var push = new FakePushNotificationService();

        await CreateService(db, push).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Empty(push.Notifications);
    }

    [Fact]
    public async Task DuplicateResync_DoesNotNotifyAgain()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedInboxAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("hello")
        });
        var push = new FakePushNotificationService();
        var service = CreateService(db, push);
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Single(push.Notifications);
    }

    [Fact]
    public async Task PushFailure_MailStillCommittedAndCheckpointAdvanced()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedInboxAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("hello")
        });
        var push = new FakePushNotificationService
        {
            Failure = _ => new InvalidOperationException("firebase down")
        };

        await CreateService(db, push).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Single(await db.Mails.ToListAsync());
        Assert.Equal(100u, (await db.SyncStates.SingleAsync()).LastUid);
    }

    [Fact]
    public async Task FlagOnlyResync_DoesNotNotify()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedInboxAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>>
        {
            [100] = () => SimpleMessage("hello")
        });
        var push = new FakePushNotificationService();
        var service = CreateService(db, push);
        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        push.Notifications.Clear();
        var mail = await db.Mails.SingleAsync();
        mail.IsRead = !mail.IsRead;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Empty(push.Notifications);
    }

    private static async Task<(Guid AccountId, Guid FolderId)> SeedInboxAsync(AppDbContext db)
    {
        var (accountId, folderId) = await SeedFolderAsync(db);
        var folder = await db.MailFolders.SingleAsync(f => f.Id == folderId);
        folder.FolderType = MailFolderType.Inbox;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId);
    }

    private static async Task<(Guid AccountId, Guid FolderId)> SeedFolderOfTypeAsync(AppDbContext db, MailFolderType type)
    {
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"seed-{userId:N}@example.test",
            PasswordHash = "seed",
            DisplayName = "Seed"
        });
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            UserId = userId,
            EmailAddress = "a@example.test",
            DisplayName = "A",
            Username = "a",
            EncryptedPassword = "x",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "Sent",
            FullName = "Sent",
            FolderType = type,
            IsSyncEnabled = true
        });
        db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailFolderId = folderId, UidValidity = 7, LastUid = 0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId);
    }

    private static MailFolderSyncService CreateService(AppDbContext db, FakePushNotificationService push) =>
        new(db,
            new PassthroughProtector(),
            null!,
            new FakeFileStorage(),
            new MailSyncOptions
            {
                Enabled = true,
                PollIntervalSeconds = 30,
                FlagSyncIntervalSeconds = 3600,
                MaxMessagesPerRun = 10,
                MaxAttachmentBytes = 500,
                MaxMessageAttachmentBytes = 800,
                MaxMessageBytes = 1000
            },
            push,
            NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class PassthroughProtector : MailClient.Application.Interfaces.ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
