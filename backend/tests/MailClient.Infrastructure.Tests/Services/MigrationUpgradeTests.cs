using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace MailClient.Infrastructure.Tests.Services;

[CollectionDefinition("postgres-upgrade")]
public sealed class PostgresUpgradeCollection : ICollectionFixture<PostgresUpgradeFixture>;

public sealed class PostgresUpgradeFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("mailclient_upgradetests")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync() => await _postgres.StartAsync();
    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}

[Collection("postgres-upgrade")]
public sealed class MigrationUpgradeTests(PostgresUpgradeFixture fixture)
{
    [Fact]
    public async Task PreAvailabilityFolder_BecomesAvailable_AfterUpgrade()
    {
        await using (var db = fixture.CreateDb())
            await db.Database.MigrateAsync("AddSendOperations");

        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        await using (var db = fixture.CreateDb())
        {
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "Users" ("Id", "Email", "PasswordHash", "DisplayName", "Role", "Status", "CreatedAt", "TokenVersion") VALUES ({0}, 'upgrade@example.test', 'seed', 'Upgrade', 0, 0, NOW(), 0)""",
                userId);
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "MailAccounts" ("Id", "UserId", "EmailAddress", "DisplayName", "Username", "EncryptedPassword", "ImapHost", "ImapPort", "ImapSecurity", "SmtpHost", "SmtpPort", "SmtpSecurity", "SaveSentCopy", "IsActive", "CreatedAt", "UpdatedAt") VALUES ({0}, {1}, 'u@example.test', 'U', 'u', 'x', 'imap.example.test', 993, 1, 'smtp.example.test', 587, 2, TRUE, TRUE, NOW(), NOW())""",
                accountId, userId);
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "MailFolders" ("Id", "MailAccountId", "Name", "FullName", "FolderType", "UidValidity", "IsSyncEnabled") VALUES ({0}, {1}, 'INBOX', 'INBOX', 0, 7, TRUE)""",
                folderId, accountId);
        }

        await using (var db = fixture.CreateDb())
            await db.Database.MigrateAsync();

        await using var check = fixture.CreateDb();
        var folder = await check.MailFolders.SingleAsync(item => item.Id == folderId);
        Assert.True(folder.IsAvailable);
        Assert.True(folder.IsSyncEnabled);

        check.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Name = "New",
            FullName = "New",
            IsSyncEnabled = true
        });
        await check.SaveChangesAsync();
        Assert.True((await check.MailFolders.SingleAsync(item => item.Name == "New")).IsAvailable);
    }
}
