using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class MailFolderRefreshRaceTests
{
    [Fact]
    public async Task RefreshAsync_ReconfiguredDuringDiscovery_DiscardsStaleFolders()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await using (var seed = CreateDb(dbName))
        {
            seed.MailAccounts.Add(new MailAccount
            {
                Id = accountId,
                UserId = userId,
                Username = "user@old.test",
                EncryptedPassword = "secret",
                ImapHost = "imap.old.test",
                ImapPort = 993,
                ImapSecurity = MailSecurity.SslOnConnect,
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync();
        }

        await using var db = CreateDb(dbName);
        var explorer = new ReconfiguringExplorer(dbName, accountId);
        var service = new MailFolderService(
            db, new PassthroughProtector(), explorer, NullLogger<MailFolderService>.Instance);

        var result = await service.RefreshAsync(userId, accountId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Empty(await db.MailFolders.ToListAsync());
    }

    [Fact]
    public async Task RefreshAsync_UndisturbedDiscovery_CommitsFolders()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await using (var seed = CreateDb(dbName))
        {
            seed.MailAccounts.Add(new MailAccount
            {
                Id = accountId,
                UserId = userId,
                Username = "user@mail.test",
                EncryptedPassword = "secret",
                ImapHost = "imap.mail.test",
                ImapPort = 993,
                ImapSecurity = MailSecurity.SslOnConnect,
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync();
        }

        await using var db = CreateDb(dbName);
        var service = new MailFolderService(
            db,
            new PassthroughProtector(),
            new StaticExplorer([new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true)]),
            NullLogger<MailFolderService>.Instance);

        var result = await service.RefreshAsync(userId, accountId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Equal("INBOX", Assert.Single(await db.MailFolders.ToListAsync()).FullName);
    }

    private static AppDbContext CreateDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class StaticExplorer(IReadOnlyList<DiscoveredMailFolder> folders) : IMailFolderExplorer
    {
        public Task<IReadOnlyList<DiscoveredMailFolder>> ExploreAsync(
            MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Task.FromResult(folders);
    }

    private sealed class ReconfiguringExplorer(string dbName, Guid accountId) : IMailFolderExplorer
    {
        public async Task<IReadOnlyList<DiscoveredMailFolder>> ExploreAsync(
            MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken)
        {
            await using var intruder = CreateDb(dbName);
            var account = await intruder.MailAccounts.SingleAsync(item => item.Id == accountId, cancellationToken);
            account.ImapHost = "imap.new.test";
            account.UpdatedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
            await intruder.SaveChangesAsync(cancellationToken);

            return [new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true)];
        }
    }
}
