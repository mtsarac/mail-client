using MailClient.Application.Authentication;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MailClient.Tests;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TestDatabase = "mailclient_v2_tests";

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var admin = IntegrationEnvironment.PostgresAdmin!;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
            exists.Parameters.AddWithValue("name", TestDatabase);
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{TestDatabase}\"";
                await create.ExecuteNonQueryAsync();
            }
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = TestDatabase }.ConnectionString;
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE "MailAccounts", "AuditLogs" CASCADE""");
    }

    public AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public Task DisposeAsync() => Task.CompletedTask;
}

[Collection("postgres")]
public sealed class PostgresIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RefreshRotation_ConcurrentSameToken_ProducesSingleWinner()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        string token;
        await using (var db = fixture.CreateDb())
        {
            token = (await new MailSessionService(db, new SessionOptions()).CreateAsync(accountId, null, TimeSpan.FromDays(1), CancellationToken.None)).Token;
        }

        await using var first = fixture.CreateDb();
        await using var second = fixture.CreateDb();
        var results = await Task.WhenAll(
            new MailSessionService(first, new SessionOptions()).RotateAsync(token, TimeSpan.FromDays(1), true, CancellationToken.None),
            new MailSessionService(second, new SessionOptions()).RotateAsync(token, TimeSpan.FromDays(1), true, CancellationToken.None));

        Assert.Equal(1, results.Count(static result => result is not null));
        await using var check = fixture.CreateDb();
        Assert.Equal(1, await check.MailSessions.CountAsync(x => x.MailAccountId == accountId && x.RevokedAt == null));
    }

    [Fact]
    public async Task RefreshRotation_MalformedAndUnknownTokens_AreRejectedNotThrown()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        await using var db = fixture.CreateDb();
        var service = new MailSessionService(db, new SessionOptions());

        Assert.Null(await service.RotateAsync("not-base64!!", TimeSpan.FromDays(1), true, CancellationToken.None));
        Assert.Null(await service.RotateAsync(Convert.ToBase64String(new byte[64]), TimeSpan.FromDays(1), true, CancellationToken.None));
        Assert.False(await service.RevokeAsync("not-base64!!", CancellationToken.None));
    }

    [Fact]
    public async Task NormalizedEmailAddress_UniqueIndex_IsEnforcedByPostgres()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var email = $"dup-{Guid.NewGuid():N}@example.test";
        await using var db = fixture.CreateDb();
        db.MailAccounts.Add(Account(email));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.MailAccounts.Add(Account(email));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Mail_FolderUid_UniqueIndex_IsEnforcedByPostgres()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var folderId = Guid.NewGuid();
        await using var db = fixture.CreateDb();
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
        await db.SaveChangesAsync();

        db.Mails.Add(Mail(accountId, folderId, 42));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Mails.Add(Mail(accountId, folderId, 42));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SendIdempotency_ConcurrentSameKey_ProducesSingleProceed()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var fingerprint = SendOperationStore.Fingerprint(accountId, "friend@example.test", "Hi", null, "x", []);
        var key = $"pg-key-{Guid.NewGuid():N}";
        await using var first = fixture.CreateDb();
        await using var second = fixture.CreateDb();

        var claims = await Task.WhenAll(
            new SendOperationStore(first, NullLogger<SendOperationStore>.Instance).ClaimAsync(accountId, key, fingerprint, CancellationToken.None),
            new SendOperationStore(second, NullLogger<SendOperationStore>.Instance).ClaimAsync(accountId, key, fingerprint, CancellationToken.None));

        Assert.Equal(1, claims.Count(static claim => claim is SendOperationStore.Proceed));
    }

    [Fact]
    public async Task AccountDeletion_CascadesOwnedRows_AndNullsAuditLogs()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        await using (var db = fixture.CreateDb())
        {
            var folderId = Guid.NewGuid();
            var mailId = Guid.NewGuid();
            var sendOperationId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
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
                Uid = 1,
                UidValidity = 7,
                Subject = "cascade",
                FromAddress = "a@example.test",
                ReceivedAt = DateTime.UtcNow
            });
            db.Attachments.Add(new Attachment
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                MailId = mailId,
                FileName = "file.txt",
                ContentType = "text/plain",
                StoragePath = "attachments/x",
                SizeBytes = 3
            });
            db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, UidValidity = 7 });
            db.SyncSkippedUids.Add(new SyncSkippedUid { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, Uid = 9, Reason = "gone" });
            db.SendOperations.Add(new SendOperation { Id = sendOperationId, MailAccountId = accountId, IdempotencyKey = "key", Fingerprint = "fp" });
            db.DeviceTokens.Add(new DeviceToken { Id = deviceId, MailAccountId = accountId, Token = "tok", Platform = "ios" });
            db.MailSessions.Add(new MailSession
            {
                Id = sessionId,
                MailAccountId = accountId,
                RefreshTokenHash = new string('h', 64),
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            });
            db.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), MailAccountId = accountId, Action = "test.action", EntityType = "MailAccount", TimestampUtc = DateTime.UtcNow });
            db.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), MailAccountId = null, Action = "other.action", EntityType = "MailAccount", TimestampUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            db.Remove(await db.MailAccounts.SingleAsync(x => x.Id == accountId));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        await using var check = fixture.CreateDb();
        Assert.Equal(0, await check.MailAccounts.CountAsync(x => x.Id == accountId));
        Assert.Equal(0, await check.MailFolders.CountAsync(x => x.MailAccountId == accountId));
        Assert.Equal(0, await check.Mails.CountAsync(x => x.MailAccountId == accountId));
        Assert.Equal(0, await check.Attachments.CountAsync(x => x.MailAccountId == accountId));
        Assert.Equal(0, await check.SyncStates.CountAsync(x => x.MailAccountId == accountId));
        Assert.Equal(0, await check.SyncSkippedUids.CountAsync(x => x.MailAccountId == accountId));
        Assert.Equal(0, await check.SendOperations.CountAsync(x => x.MailAccountId == accountId));
        Assert.Equal(0, await check.DeviceTokens.CountAsync(x => x.MailAccountId == accountId));
        Assert.Equal(0, await check.MailSessions.CountAsync(x => x.MailAccountId == accountId));
        Assert.Equal(1, await check.AuditLogs.CountAsync(x => x.MailAccountId == null && x.Action == "test.action"));
        Assert.Equal(1, await check.AuditLogs.CountAsync(x => x.MailAccountId == null && x.Action == "other.action"));
    }

    private async Task<Guid> SeedAccountAsync()
    {
        var account = Account($"seed-{Guid.NewGuid():N}@example.test");
        await using var db = fixture.CreateDb();
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static MailAccount Account(string email) => new()
    {
        Id = Guid.NewGuid(),
        EmailAddress = email,
        NormalizedEmailAddress = email.ToUpperInvariant(),
        DisplayName = "Seed",
        Username = email,
        Provider = MailProvider.Custom,
        AuthenticationMethod = AuthenticationMethod.Password,
        ImapHost = "imap.example.test",
        ImapPort = 993,
        ImapSecurity = MailSecurity.SslOnConnect,
        SmtpHost = "smtp.example.test",
        SmtpPort = 465,
        SmtpSecurity = MailSecurity.SslOnConnect,
        DiscoverySource = DiscoverySource.Manual,
        Status = MailAccountStatus.Active
    };

    private static Domain.Entities.Mail Mail(Guid accountId, Guid folderId, uint uid) => new()
    {
        Id = Guid.NewGuid(),
        MailAccountId = accountId,
        MailFolderId = folderId,
        Uid = uid,
        UidValidity = 7,
        Subject = "pg",
        FromAddress = "a@example.test",
        ReceivedAt = DateTime.UtcNow
    };
}
