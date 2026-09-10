using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using MailClient.Application.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class MailFolderAvailabilityTests
{
    [Fact]
    public async Task Upsert_InsertsDiscovered_AsAvailable()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateFolderService(db);

        var result = await service.UpsertAsync(accountId,
        [
            new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true)
        ], CancellationToken.None);

        var folder = await db.MailFolders.SingleAsync();
        Assert.True(folder.IsAvailable);
        Assert.True(folder.IsSyncEnabled);
        Assert.True(result.Single().IsAvailable);
    }

    [Fact]
    public async Task Upsert_UpdatesExisting_PreservesExplicitSyncPreference()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateFolderService(db);
        await service.UpsertAsync(accountId,
            [new DiscoveredMailFolder("Archive", "Archive", MailFolderType.Archive, 7, false)],
            CancellationToken.None);
        var folderId = (await db.MailFolders.SingleAsync()).Id;
        var userId = (await db.MailAccounts.SingleAsync(item => item.Id == accountId)).UserId;
        await service.SetSyncEnabledAsync(userId, accountId, folderId, true, CancellationToken.None);

        await service.UpsertAsync(accountId,
            [new DiscoveredMailFolder("Archive", "Archive", MailFolderType.Archive, 8, false)],
            CancellationToken.None);

        var folder = await db.MailFolders.SingleAsync();
        Assert.Equal(8u, folder.UidValidity);
        Assert.True(folder.IsSyncEnabled);
        Assert.True(folder.IsAvailable);
    }

    [Fact]
    public async Task Upsert_MissingFolder_BecomesUnavailable_KeepsMailAndPreference()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateFolderService(db);
        await service.UpsertAsync(accountId,
        [
            new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true),
            new DiscoveredMailFolder("Old", "Old", MailFolderType.Custom, 5, true)
        ], CancellationToken.None);
        var oldId = (await db.MailFolders.SingleAsync(item => item.FullName == "Old")).Id;
        db.Mails.Add(new Mail
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            MailFolderId = oldId,
            Uid = 1,
            UidValidity = 5,
            MessageId = "m@example.test",
            Subject = "historic",
            FromAddress = "a@example.test",
            ToAddress = "b@example.test",
            ReceivedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await service.UpsertAsync(accountId,
            [new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true)],
            CancellationToken.None);

        var old = await db.MailFolders.SingleAsync(item => item.FullName == "Old");
        Assert.False(old.IsAvailable);
        Assert.True(old.IsSyncEnabled);
        Assert.Single(await db.Mails.ToListAsync());
    }

    [Fact]
    public async Task Upsert_ReappearingFolder_BecomesAvailable_WithoutChangingPreference()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateFolderService(db);
        var userId = (await db.MailAccounts.SingleAsync(item => item.Id == accountId)).UserId;
        await service.UpsertAsync(accountId,
            [new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true)],
            CancellationToken.None);
        var folderId = (await db.MailFolders.SingleAsync()).Id;
        await service.SetSyncEnabledAsync(userId, accountId, folderId, false, CancellationToken.None);
        await service.UpsertAsync(accountId, [], CancellationToken.None);
        Assert.False((await db.MailFolders.SingleAsync()).IsAvailable);

        await service.UpsertAsync(accountId,
            [new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true)],
            CancellationToken.None);

        var folder = await db.MailFolders.SingleAsync();
        Assert.True(folder.IsAvailable);
        Assert.False(folder.IsSyncEnabled);
    }

    [Fact]
    public async Task SyncableFolders_ExcludesUnavailable()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var folderService = CreateFolderService(db);
        await folderService.UpsertAsync(accountId,
        [
            new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true),
            new DiscoveredMailFolder("Gone", "Gone", MailFolderType.Custom, 5, true)
        ], CancellationToken.None);
        await folderService.UpsertAsync(accountId,
            [new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true)],
            CancellationToken.None);
        var sync = new MailFolderSyncService(db, new PassthroughProtector(), null!,
            new FakeFileStorage(), TestOptions(), NullLogger<MailFolderSyncService>.Instance);

        var syncable = await sync.GetSyncableFoldersAsync(accountId, CancellationToken.None);

        Assert.Single(syncable);
        Assert.Equal("INBOX", syncable[0].FullName);
    }

    private static MailFolderService CreateFolderService(AppDbContext db) =>
        new(db, new PassthroughProtector(), new EmptyExplorer(), NullLogger<MailFolderService>.Instance);

    private static MailSyncOptions TestOptions() => new()
    {
        Enabled = true,
        PollIntervalSeconds = 30,
        FlagSyncIntervalSeconds = 120,
        MaxMessagesPerRun = 10,
        MaxAttachmentBytes = 500,
        MaxMessageAttachmentBytes = 800,
        MaxMessageBytes = 1000
    };

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<Guid> SeedAccountAsync(AppDbContext db)
    {
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"folder-{userId:N}@example.test",
            PasswordHash = "seed",
            DisplayName = "Folder"
        });
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            UserId = userId,
            EmailAddress = "f@example.test",
            DisplayName = "F",
            Username = "f",
            EncryptedPassword = "x",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return accountId;
    }

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class EmptyExplorer : IMailFolderExplorer
    {
        public Task<IReadOnlyList<DiscoveredMailFolder>> ExploreAsync(
            Application.Network.MailServerEndpoint endpoint, string username, string password,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveredMailFolder>>([]);
    }
}
