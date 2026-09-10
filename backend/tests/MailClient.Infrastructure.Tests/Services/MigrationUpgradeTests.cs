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

    [Fact]
    public async Task ScanCursor_BackfillsFromLastUid_AfterUpgrade()
    {
        await using (var db = fixture.CreateDb())
            await db.Database.MigrateAsync("AddMailFolderAvailability");

        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var expectations = new (string Name, long LastUid, long Cursor)[]
        {
            ("A", 0L, 1L),
            ("B", 500L, 501L),
            ("C", 100000000L, 100000001L),
            ("D", 4294967295L, 4294967296L)
        };
        await using (var db = fixture.CreateDb())
        {
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "Users" ("Id", "Email", "PasswordHash", "DisplayName", "Role", "Status", "CreatedAt", "TokenVersion") VALUES ({0}, 'cursor@example.test', 'seed', 'Cursor', 0, 0, NOW(), 0)""",
                userId);
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "MailAccounts" ("Id", "UserId", "EmailAddress", "DisplayName", "Username", "EncryptedPassword", "ImapHost", "ImapPort", "ImapSecurity", "SmtpHost", "SmtpPort", "SmtpSecurity", "SaveSentCopy", "IsActive", "CreatedAt", "UpdatedAt") VALUES ({0}, {1}, 'c@example.test', 'C', 'c', 'x', 'imap.example.test', 993, 1, 'smtp.example.test', 587, 2, TRUE, TRUE, NOW(), NOW())""",
                accountId, userId);
            foreach (var (name, lastUid, _) in expectations)
            {
                var folderId = Guid.NewGuid();
                var stateId = Guid.NewGuid();
                await db.Database.ExecuteSqlRawAsync(
                    """INSERT INTO "MailFolders" ("Id", "MailAccountId", "Name", "FullName", "FolderType", "UidValidity", "IsSyncEnabled") VALUES ({0}, {1}, {2}, {2}, 0, 7, TRUE)""",
                    folderId, accountId, name);
                await db.Database.ExecuteSqlRawAsync(
                    """INSERT INTO "SyncStates" ("Id", "MailFolderId", "UidValidity", "LastUid") VALUES ({0}, {1}, 7, {2})""",
                    stateId, folderId, lastUid);
            }
        }

        await using (var db = fixture.CreateDb())
            await db.Database.MigrateAsync();

        await using var check = fixture.CreateDb();
        foreach (var (name, lastUid, cursor) in expectations)
        {
            var folderId = await check.MailFolders
                .Where(item => item.Name == name)
                .Select(item => item.Id)
                .SingleAsync();
            var state = await check.SyncStates.SingleAsync(item => item.MailFolderId == folderId);
            Assert.Equal(lastUid, (long)state.LastUid);
            Assert.Equal(cursor, state.NextUidScanStart);
        }

        await check.Database.ExecuteSqlRawAsync(
            """INSERT INTO "MailFolders" ("Id", "MailAccountId", "Name", "FullName", "FolderType", "UidValidity", "IsSyncEnabled") VALUES ({0}, {1}, 'DefaultCursor', 'DefaultCursor', 0, 7, TRUE)""",
            Guid.NewGuid(), accountId);
        var defaultFolderId = await check.MailFolders
            .Where(item => item.Name == "DefaultCursor")
            .Select(item => item.Id)
            .SingleAsync();
        await check.Database.ExecuteSqlRawAsync(
            """INSERT INTO "SyncStates" ("Id", "MailFolderId", "UidValidity", "LastUid") VALUES ({0}, {1}, 7, 0)""",
            Guid.NewGuid(), defaultFolderId);
        var defaultCursor = await check.SyncStates
            .Where(item => item.MailFolderId == defaultFolderId)
            .Select(item => item.NextUidScanStart)
            .SingleAsync();
        Assert.Equal(1L, defaultCursor);
    }
}
