using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using MailKit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Tests.Services;

public class MailFolderDiscoveryTests
{
    [Theory]
    [InlineData(FolderAttributes.Inbox, "archive", MailFolderType.Inbox, true)]
    [InlineData(FolderAttributes.Sent, "archive", MailFolderType.Sent, true)]
    [InlineData(FolderAttributes.Drafts, "archive", MailFolderType.Drafts, false)]
    [InlineData(FolderAttributes.Trash, "archive", MailFolderType.Trash, false)]
    [InlineData(FolderAttributes.Junk, "archive", MailFolderType.Junk, false)]
    [InlineData(FolderAttributes.Archive, "archive", MailFolderType.Archive, false)]
    [InlineData(FolderAttributes.None, "INBOX", MailFolderType.Inbox, true)]
    [InlineData(FolderAttributes.None, "Sent", MailFolderType.Sent, true)]
    [InlineData(FolderAttributes.None, "Projects", MailFolderType.Custom, false)]
    public void Classify_UsesSpecialUseBeforeMinimalFallback(
        FolderAttributes attributes,
        string fullName,
        MailFolderType expectedType,
        bool expectedSyncEnabled)
    {
        var result = MailFolderDiscovery.Classify(attributes, fullName);

        Assert.Equal(expectedType, result.FolderType);
        Assert.Equal(expectedSyncEnabled, result.IsSyncEnabled);
    }

    [Fact]
    public async Task ListAsync_DoesNotReturnAnotherUsersFolders()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var account = new MailAccount { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        db.MailAccounts.Add(account);
        db.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            Name = "Inbox",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsSyncEnabled = true
        });
        await db.SaveChangesAsync();

        var result = await new MailFolderService(db, CreateProtector()).ListAsync(Guid.NewGuid(), account.Id, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SetSyncEnabledAsync_UpdatesOnlyTheOwnedFolder()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var userId = Guid.NewGuid();
        var account = new MailAccount { Id = Guid.NewGuid(), UserId = userId };
        var folder = new MailClient.Domain.Entities.MailFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            Name = "Archive",
            FullName = "Archive",
            FolderType = MailFolderType.Archive
        };
        db.AddRange(account, folder);
        await db.SaveChangesAsync();

        var result = await new MailFolderService(db, CreateProtector()).SetSyncEnabledAsync(userId, account.Id, folder.Id, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsSyncEnabled);
        Assert.True((await db.MailFolders.SingleAsync()).IsSyncEnabled);
    }

    [Fact]
    public async Task UpsertAsync_CreatesInboxEnabledAndPreservesExplicitToggle()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var account = new MailAccount { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        var service = new MailFolderService(db, CreateProtector());

        await service.UpsertAsync(account.Id,
        [
            new DiscoveredMailFolder("INBOX", "INBOX", FolderAttributes.Inbox, 100),
            new DiscoveredMailFolder("Archive", "Archive", FolderAttributes.Archive, 7),
        ], CancellationToken.None);

        var folders = await db.MailFolders.OrderBy(folder => folder.FullName).ToListAsync();
        Assert.Equal(2, folders.Count);
        Assert.True(folders.Single(folder => folder.FullName == "INBOX").IsSyncEnabled);
        Assert.False(folders.Single(folder => folder.FullName == "Archive").IsSyncEnabled);

        await service.SetSyncEnabledAsync(account.UserId, account.Id,
            folders.Single(folder => folder.FullName == "Archive").Id, true, CancellationToken.None);

        await service.UpsertAsync(account.Id,
        [
            new DiscoveredMailFolder("INBOX", "INBOX", FolderAttributes.Inbox, 101),
            new DiscoveredMailFolder("Archive", "Archive", FolderAttributes.Archive, 7),
        ], CancellationToken.None);

        folders = await db.MailFolders.OrderBy(folder => folder.FullName).ToListAsync();
        Assert.Equal(2, folders.Count);
        Assert.Equal(101u, folders.Single(folder => folder.FullName == "INBOX").UidValidity);
        Assert.True(folders.Single(folder => folder.FullName == "Archive").IsSyncEnabled);
    }

    private static ICredentialProtector CreateProtector() => new DataProtectionCredentialProtector(
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))));
}
