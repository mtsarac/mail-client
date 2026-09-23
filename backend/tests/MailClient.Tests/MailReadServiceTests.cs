using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class MailReadServiceTests
{
    [Fact]
    public async Task GetAsync_ReturnsNullForDifferentAccount()
    {
        await using var db = CreateDb();
        var mail = new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = Guid.NewGuid(), MailFolderId = Guid.NewGuid(), Subject = "private" };
        db.Mails.Add(mail);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeReadFolder(new FakeRemoteMailFolder(7, new())));

        Assert.Null(await service.GetAsync(Guid.NewGuid(), mail.Id, CancellationToken.None));
        Assert.NotNull(await service.GetAsync(mail.MailAccountId, mail.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("a@example.test", true)]
    [InlineData("A@Example.Test", true)]
    [InlineData("someone@example.test", false)]
    public async Task GetAsync_IsFromMe_MatchesAccountAddressOutsideSentFolder(string fromAddress, bool expected)
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedMailAsync(db, isRead: false);
        var mail = await db.Mails.SingleAsync(x => x.Id == mailId);
        mail.FromAddress = fromAddress;
        await db.SaveChangesAsync();

        var detail = await CreateService(db, new FakeReadFolder(new FakeRemoteMailFolder(7, new()))).GetAsync(accountId, mailId, CancellationToken.None);

        Assert.Equal(expected, detail!.IsFromMe);
    }

    [Fact]
    public async Task SetReadAsync_SameState_NoRemoteCall()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedMailAsync(db, isRead: true);
        var folders = new FakeReadFolder(new FakeRemoteMailFolder(7, new()));

        var outcome = await CreateService(db, folders).SetReadAsync(accountId, mailId, true, null, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.False(outcome.Applied);
        Assert.False(folders.Used);
    }

    [Fact]
    public async Task SetReadAsync_RemoteFirst_ThenCacheAndAudit()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedMailAsync(db, isRead: false);
        var remote = new FakeRemoteMailFolder(7, new());
        var service = CreateService(db, new FakeReadFolder(remote));

        var outcome = await service.SetReadAsync(accountId, mailId, true, "corr-1", CancellationToken.None);

        Assert.True(outcome is { Found: true, Applied: true, Conflict: false, ProviderError: false });
        Assert.True((await db.Mails.SingleAsync(x => x.Id == mailId)).IsRead);
        var audit = await db.AuditLogs.SingleOrDefaultAsync(x => x.Action == "mail.read-state-changed" && x.MailAccountId == accountId);
        Assert.NotNull(audit);
        Assert.Equal("corr-1", audit.CorrelationId);
    }

    [Fact]
    public async Task SetReadAsync_UidValidityChanged_ConflictWithoutCacheChange()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedMailAsync(db, isRead: false);
        var remote = new FakeRemoteMailFolder(999, new());
        var service = CreateService(db, new FakeReadFolder(remote));

        var outcome = await service.SetReadAsync(accountId, mailId, true, null, CancellationToken.None);

        Assert.True(outcome is { Found: true, Applied: false, Conflict: true });
        Assert.False((await db.Mails.SingleAsync(x => x.Id == mailId)).IsRead);
    }

    [Fact]
    public async Task SetReadAsync_ProviderFailure_ProviderError()
    {
        await using var db = CreateDb();
        var (accountId, _, mailId) = await SeedMailAsync(db, isRead: false);
        var service = CreateService(db, new ThrowingReadFolder());

        var outcome = await service.SetReadAsync(accountId, mailId, true, null, CancellationToken.None);

        Assert.True(outcome is { Found: true, Applied: false, ProviderError: true });
        Assert.False((await db.Mails.SingleAsync(x => x.Id == mailId)).IsRead);
    }

    private static MailReadService CreateService(AppDbContext db, IMailFolderClient folders) =>
        new(db, folders, new AuditLogger(db), NullLogger<MailReadService>.Instance);

    private static async Task<(Guid AccountId, Guid FolderId, Guid MailId)> SeedMailAsync(AppDbContext db, bool isRead)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "a@example.test",
            NormalizedEmailAddress = "A@EXAMPLE.TEST",
            Username = "a",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsSyncEnabled = true,
            IsAvailable = true
        });
        db.Mails.Add(new Domain.Entities.Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = folderId,
            Uid = 5,
            UidValidity = 7,
            Subject = "hi",
            IsRead = isRead
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId, mailId);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeReadFolder(FakeRemoteMailFolder remote) : IMailFolderClient
    {
        public bool Used { get; private set; }
        public Task<T> UseFolderAsync<T>(MailAccount account, string fullName, bool forUpdate,
            Func<IRemoteMailFolder, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        {
            Used = true;
            return action(remote, cancellationToken);
        }
    }

    private sealed class ThrowingReadFolder : IMailFolderClient
    {
        public Task<T> UseFolderAsync<T>(MailAccount account, string fullName, bool forUpdate,
            Func<IRemoteMailFolder, CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
            throw new MailConnectionException(MailConnectionFailure.Network, "down");
    }
}
