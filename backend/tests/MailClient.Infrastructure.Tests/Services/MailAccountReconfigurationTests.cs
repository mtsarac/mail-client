using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Validation;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class MailAccountReconfigurationTests
{
    [Fact]
    public async Task SmtpOnlyUpdate_PreservesCache()
    {
        await using var db = CreateDb();
        var storage = new TrackingFileStorage();
        var service = CreateService(db, storage);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);
        await SeedCacheAsync(db, account.Id, "cached/file-a");

        var updated = await service.UpdateAsync(userId, account.Id,
            UpdateRequest() with { SmtpHost = "smtp.other.test", SmtpPort = 2525, SmtpSecurity = MailSecurity.SslOnConnect },
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("smtp.other.test", updated.SmtpHost);
        Assert.Equal(1, await db.MailFolders.CountAsync(f => f.MailAccountId == account.Id));
        Assert.Equal(1, await db.Mails.CountAsync(m => m.MailAccountId == account.Id));
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task DisplayNameOnlyUpdate_PreservesCache()
    {
        await using var db = CreateDb();
        var storage = new TrackingFileStorage();
        var service = CreateService(db, storage);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);
        await SeedCacheAsync(db, account.Id, "cached/file-a");

        var updated = await service.UpdateAsync(userId, account.Id,
            UpdateRequest() with { DisplayName = "Renamed" }, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated.DisplayName);
        Assert.Equal(1, await db.MailFolders.CountAsync(f => f.MailAccountId == account.Id));
        Assert.Equal(1, await db.Mails.CountAsync(m => m.MailAccountId == account.Id));
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task PasswordOnlyUpdate_PreservesCacheAndRotatesCredential()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        var storage = new TrackingFileStorage();
        var service = CreateService(db, storage, protector);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);
        await SeedCacheAsync(db, account.Id, "cached/file-a");

        await service.UpdateAsync(userId, account.Id,
            UpdateRequest() with { Password = "rotated-secret" }, CancellationToken.None);

        Assert.Equal(1, await db.MailFolders.CountAsync(f => f.MailAccountId == account.Id));
        Assert.Equal(1, await db.Mails.CountAsync(m => m.MailAccountId == account.Id));
        Assert.Empty(storage.Deleted);
        Assert.Equal("rotated-secret", protector.Unprotect((await db.MailAccounts.SingleAsync()).EncryptedPassword));
    }

    [Fact]
    public async Task EmailOnlyUpdate_PreservesCache()
    {
        await using var db = CreateDb();
        var storage = new TrackingFileStorage();
        var service = CreateService(db, storage);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);
        await SeedCacheAsync(db, account.Id, "cached/file-a");

        var updated = await service.UpdateAsync(userId, account.Id,
            UpdateRequest() with { EmailAddress = "renamed@example.com" }, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(1, await db.MailFolders.CountAsync(f => f.MailAccountId == account.Id));
        Assert.Equal(1, await db.Mails.CountAsync(m => m.MailAccountId == account.Id));
        Assert.Empty(storage.Deleted);
    }

    [Theory]
    [InlineData("imap.other.test", 993, MailSecurity.SslOnConnect, "account@example.com")]
    [InlineData("imap.example.com", 143, MailSecurity.SslOnConnect, "account@example.com")]
    [InlineData("imap.example.com", 993, MailSecurity.StartTls, "account@example.com")]
    [InlineData("imap.example.com", 993, MailSecurity.SslOnConnect, "other@example.com")]
    public async Task ImapIdentityChange_ResetsCacheButPreservesAccount(
        string host, int port, MailSecurity security, string username)
    {
        await using var db = CreateDb();
        var storage = new TrackingFileStorage();
        var service = CreateService(db, storage);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);
        await SeedCacheAsync(db, account.Id, "cached/file-a");

        var updated = await service.UpdateAsync(userId, account.Id,
            UpdateRequest() with { ImapHost = host, ImapPort = port, ImapSecurity = security, Username = username },
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(host, updated.ImapHost);
        Assert.Equal(0, await db.MailFolders.CountAsync(f => f.MailAccountId == account.Id));
        Assert.Equal(0, await db.Mails.CountAsync(m => m.MailAccountId == account.Id));
        Assert.Equal(0, await db.Attachments.CountAsync());
        Assert.Equal(0, await db.SyncSkippedUids.CountAsync());
        Assert.Equal(0, await db.SyncStates.CountAsync());
        Assert.Contains("cached/file-a", storage.Deleted);
        var stored = await db.MailAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal(host, stored.ImapHost);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task ImapIdentityChange_RemovesOnlyOwnAttachmentFiles()
    {
        await using var db = CreateDb();
        var storage = new TrackingFileStorage();
        var service = CreateService(db, storage);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);
        var other = await service.CreateAsync(userId, CreateRequest("secret-2") with
        {
            EmailAddress = "other@example.com",
            Username = "other@example.com"
        }, CancellationToken.None);
        await SeedCacheAsync(db, account.Id, "mine/file-a");
        await SeedCacheAsync(db, other.Id, "theirs/file-b");

        await service.UpdateAsync(userId, account.Id,
            UpdateRequest() with { ImapHost = "imap.other.test" }, CancellationToken.None);

        Assert.Contains("mine/file-a", storage.Deleted);
        Assert.DoesNotContain("theirs/file-b", storage.Deleted);
        Assert.Equal(1, await db.Mails.CountAsync(m => m.MailAccountId == other.Id));
    }

    [Fact]
    public async Task ImapIdentityChange_DoesNotTouchUnrelatedAccount()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new TrackingFileStorage());
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);
        var other = await service.CreateAsync(userId, CreateRequest("secret-2") with
        {
            EmailAddress = "other@example.com",
            Username = "other@example.com"
        }, CancellationToken.None);
        await SeedCacheAsync(db, account.Id, "mine/file-a");
        await SeedCacheAsync(db, other.Id, "theirs/file-b");

        await service.UpdateAsync(userId, account.Id,
            UpdateRequest() with { Username = "moved@example.com" }, CancellationToken.None);

        Assert.Equal(1, await db.MailFolders.CountAsync(f => f.MailAccountId == other.Id));
        Assert.Equal(1, await db.Mails.CountAsync(m => m.MailAccountId == other.Id));
    }

    [Fact]
    public async Task Update_OtherUsersAccount_ReturnsNull()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new TrackingFileStorage());
        var account = await service.CreateAsync(Guid.NewGuid(), CreateRequest("secret-1"), CancellationToken.None);

        var result = await service.UpdateAsync(Guid.NewGuid(), account.Id, UpdateRequest(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Create_DuplicateEmail_ThrowsConflict()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new TrackingFileStorage());
        var userId = Guid.NewGuid();
        await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);

        await Assert.ThrowsAsync<RequestConflictException>(() =>
            service.CreateAsync(userId, CreateRequest("secret-2"), CancellationToken.None));
    }

    [Fact]
    public async Task Update_DuplicateEmail_ThrowsConflict()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new TrackingFileStorage());
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, CreateRequest("secret-1"), CancellationToken.None);
        await service.CreateAsync(userId, CreateRequest("secret-2") with
        {
            EmailAddress = "other@example.com",
            Username = "other@example.com"
        }, CancellationToken.None);

        await Assert.ThrowsAsync<RequestConflictException>(() =>
            service.UpdateAsync(userId, account.Id,
                UpdateRequest() with { EmailAddress = "other@example.com" }, CancellationToken.None));
    }

    private static async Task SeedCacheAsync(AppDbContext db, Guid accountId, string storagePath)
    {
        var folderId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            IsSyncEnabled = true
        });
        db.Mails.Add(new Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = folderId,
            Subject = "cached",
            ReceivedAt = DateTime.UtcNow
        });
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(),
            MailId = mailId,
            FileName = "a.txt",
            ContentType = "text/plain",
            StoragePath = storagePath
        });
        db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailFolderId = folderId, UidValidity = 9, LastUid = 3 });
        db.SyncSkippedUids.Add(new SyncSkippedUid
        {
            Id = Guid.NewGuid(),
            MailFolderId = folderId,
            Uid = 2,
            Reason = "oversized",
            SkippedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static MailAccountService CreateService(AppDbContext db, TrackingFileStorage storage, ICredentialProtector? protector = null) =>
        new(db, protector ?? CreateProtector(), new TestEnvironment(),
            new AllowConnectivityTester(), new AllowHostValidator(), storage,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MailAccountService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static ICredentialProtector CreateProtector() => new DataProtectionCredentialProtector(
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))));

    private static MailAccountRequest CreateRequest(string password) => new(
        "account@example.com", "Account", "account@example.com", password,
        "imap.example.com", 993, MailSecurity.SslOnConnect,
        "smtp.example.com", 587, MailSecurity.StartTls, true);

    private static UpdateMailAccountRequest UpdateRequest() => new(
        "account@example.com", "Account", "account@example.com", null,
        "imap.example.com", 993, MailSecurity.SslOnConnect,
        "smtp.example.com", 587, MailSecurity.StartTls, true);

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
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

    private sealed class TrackingFileStorage : IFileStorage
    {
        public List<string> Deleted { get; } = [];
        public Task<StoredFile> SaveAsync(Guid accountId, Guid mailId, Guid attachmentId, Func<Stream, CancellationToken, Task> write, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredFile("noop", 0));
        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
        {
            Deleted.Add(relativePath);
            return Task.CompletedTask;
        }
        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(Stream.Null);
        public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
