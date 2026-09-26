using MailClient.Application.Authentication;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.OAuth;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MailClient.Tests;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TestDatabase = "kaydetmail-db-tests";

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
    public async Task Search_TextQuery_MatchesSubjectAndBody()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateDb();
        var folderId = await SeedFolderAsync(db, accountId);
        db.Mails.Add(new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, Uid = 100, Subject = "quarterly report", BodyText = "unusual orchid", FromAddress = "sender@example.test", ReceivedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());

        var result = await service.SearchAsync(accountId, new MailSearchRequest("orchid", null, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None);

        Assert.Contains(result.Items, mail => mail.Subject == "quarterly report");
    }

    [Fact]
    public async Task Search_DateFilterWithoutOffset_IsTreatedAsUtc()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateDb();
        var folderId = await SeedFolderAsync(db, accountId);
        db.Mails.Add(new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, Uid = 110, Subject = "dated", FromAddress = "sender@example.test", ReceivedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());

        // Query-string binding yields DateTimeKind.Unspecified for "2026-03-10"; timestamptz rejects that kind as-is.
        var result = await service.SearchAsync(accountId, new MailSearchRequest(null, null, null, null, null,
            new DateTime(2026, 3, 10), new DateTime(2026, 3, 11), null, null, null, 1, 20), CancellationToken.None);

        Assert.Equal("dated", Assert.Single(result.Items).Subject);
    }

    [Fact]
    public async Task Search_Query_MatchesParticipantsAndAttachmentFilename()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateDb();
        var mailId = Guid.NewGuid();
        var folderId = await SeedFolderAsync(db, accountId);
        db.Mails.Add(new Domain.Entities.Mail { Id = mailId, MailAccountId = accountId, MailFolderId = folderId, Uid = 101, Subject = "plain", FromAddress = "sender@example.test", ReceivedAt = DateTime.UtcNow });
        db.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = mailId, Type = ParticipantType.Cc, Address = "cc-search@example.test", DisplayName = "CC" });
        db.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = mailId, Type = ParticipantType.ReplyTo, Address = "reply-search@example.test", DisplayName = "Reply" });
        db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), MailAccountId = accountId, MailId = mailId, FileName = "invoice-search.pdf", StoragePath = "x" });
        await db.SaveChangesAsync();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());

        Assert.Single((await service.SearchAsync(accountId, new MailSearchRequest("cc-search", null, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None)).Items);
        Assert.Single((await service.SearchAsync(accountId, new MailSearchRequest("invoice-search", null, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None)).Items);
    }

    [Fact]
    public async Task Search_FromAndToFilters_MatchPartialAddressOrName_CaseInsensitive()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateDb();
        var folderId = await SeedFolderAsync(db, accountId);
        var match = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.Mails.Add(new Domain.Entities.Mail { Id = match, MailAccountId = accountId, MailFolderId = folderId, Uid = 111, Subject = "match", FromAddress = "alice@corp.test", FromDisplayName = "Alice Smith", ReceivedAt = DateTime.UtcNow });
        db.Mails.Add(new Domain.Entities.Mail { Id = other, MailAccountId = accountId, MailFolderId = folderId, Uid = 112, Subject = "other", FromAddress = "bob@elsewhere.test", FromDisplayName = "Bob", ReceivedAt = DateTime.UtcNow });
        db.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = match, Type = ParticipantType.Cc, Address = "carol@partner.test", NormalizedAddress = "carol@partner.test", DisplayName = "Carol" });
        db.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = other, Type = ParticipantType.ReplyTo, Address = "carol@partner.test", NormalizedAddress = "carol@partner.test", DisplayName = "Carol" });
        await db.SaveChangesAsync();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());

        async Task<string> OnlySubject(string? from, string? to) => Assert.Single((await service.SearchAsync(accountId,
            new MailSearchRequest(null, folderId, null, from, to, null, null, null, null, null, 1, 20), CancellationToken.None)).Items).Subject;

        Assert.Equal("match", await OnlySubject("SMITH", null));
        Assert.Equal("match", await OnlySubject("corp.test", null));
        Assert.Equal("match", await OnlySubject(null, "PARTNER"));
        Assert.Empty((await service.SearchAsync(accountId, new MailSearchRequest("%", folderId, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None)).Items);
    }

    [Fact]
    public async Task Search_IsAccountScoped_AppliesFilters_Paginates_AndHandlesSpecialCharacters()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var otherAccountId = await SeedAccountAsync();
        await using var db = fixture.CreateDb();
        var folderId = await SeedFolderAsync(db, accountId);
        var otherFolderId = await SeedFolderAsync(db, otherAccountId);
        var conversationId = Guid.NewGuid();
        db.Conversations.Add(new Conversation { Id = conversationId, MailAccountId = accountId, NormalizedSubject = "needle", StartedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
        db.Mails.Add(new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, ConversationId = conversationId, Uid = 201, Subject = "needle alpha", BodyText = "body", FromAddress = "person@example.test", ToAddress = "to@example.test", IsRead = false, Flagged = true, HasAttachments = true, ReceivedAt = DateTime.UtcNow.AddMinutes(-2) });
        db.Mails.Add(new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, Uid = 202, Subject = "needle beta", BodyText = "body", FromAddress = "person@example.test", ToAddress = "to@example.test", IsRead = false, Flagged = true, HasAttachments = true, ReceivedAt = DateTime.UtcNow.AddMinutes(-1) });
        db.Mails.Add(new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = otherAccountId, MailFolderId = otherFolderId, Uid = 203, Subject = "needle other", BodyText = "body", FromAddress = "person@example.test", ToAddress = "to@example.test", IsRead = false, Flagged = true, HasAttachments = true, ReceivedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());

        var first = await service.SearchAsync(accountId, new MailSearchRequest("needle", folderId, null, "person@example.test", "to@example.test", null, null, false, true, true, 1, 1), CancellationToken.None);
        var second = await service.SearchAsync(accountId, new MailSearchRequest("needle", folderId, null, "person@example.test", "to@example.test", null, null, false, true, true, 2, 1), CancellationToken.None);
        var special = await service.SearchAsync(accountId, new MailSearchRequest("'bad & query: *", null, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None);

        Assert.Equal(2, first.Total);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.Empty(special.Items);
    }

    [Fact]
    public async Task Search_LabelId_IsPaginationSafe_AndRespectsAccountOwnership()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var otherAccountId = await SeedAccountAsync();
        await using var db = fixture.CreateDb();
        var folderId = await SeedFolderAsync(db, accountId);
        var label = new MailLabel { Id = Guid.NewGuid(), MailAccountId = accountId, Name = "work", Color = 0, SortOrder = 0 };
        db.MailLabels.Add(label);
        var labeled = Mail(accountId, folderId, 301);
        labeled.Subject = "labeled oldest";
        labeled.ReceivedAt = DateTime.UtcNow.AddMinutes(-10);
        var newerUnlabeled = Mail(accountId, folderId, 302);
        newerUnlabeled.Subject = "newer unlabeled";
        newerUnlabeled.ReceivedAt = DateTime.UtcNow;
        db.Mails.AddRange(labeled, newerUnlabeled);
        db.MailLabelAssignments.Add(new MailLabelAssignment { Id = Guid.NewGuid(), MailAccountId = accountId, MailId = labeled.Id, MailLabelId = label.Id });
        await db.SaveChangesAsync();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());

        // Without the label filter, page 1 (size 1, newest-first) misses the older labeled mail -
        // exactly the pagination-unsafe behavior spec §11 wants moved server-side.
        var unfiltered = await service.SearchAsync(accountId, new MailSearchRequest(null, null, null, null, null, null, null, null, null, null, 1, 1), CancellationToken.None);
        Assert.Equal("newer unlabeled", unfiltered.Items.Single().Subject);

        // The label filter finds it directly instead of depending on where it landed in the page.
        var filtered = await service.SearchAsync(accountId, new MailSearchRequest(null, null, null, null, null, null, null, null, null, null, 1, 20, label.Id), CancellationToken.None);
        Assert.Equal("labeled oldest", Assert.Single(filtered.Items).Subject);

        // A label id owned by a different account never matches another account's mails.
        var crossAccount = await service.SearchAsync(otherAccountId, new MailSearchRequest(null, null, null, null, null, null, null, null, null, null, 1, 20, label.Id), CancellationToken.None);
        Assert.Empty(crossAccount.Items);
    }

    [Fact]
    public async Task Search_ExistingData_IsSearchableAfterMigration()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateDb();
        var folderId = await SeedFolderAsync(db, accountId);
        db.Mails.Add(new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, Uid = 301, Subject = "migrated zebra", FromAddress = "sender@example.test", ReceivedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());

        var result = await service.SearchAsync(accountId, new MailSearchRequest("zebra", null, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None);

        Assert.Single(result.Items);
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

    [Fact]
    public async Task Readiness_PostgresAvailable_Returns200()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        await using var factory = new NpgsqlApiFactory(fixture.ConnectionString);

        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"postgres\":\"Healthy\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SyncLock_SameAccountSecondHolderBlocked_DifferentAccountsProceed()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var otherAccountId = await SeedAccountAsync();
        var provider = new PostgresSyncLockProvider(fixture.ConnectionString, NullLogger<PostgresSyncLockProvider>.Instance);

        await using var first = await provider.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, CancellationToken.None);
        Assert.Equal(SyncLockStatus.Acquired, first.Status);
        await using var contended = await provider.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, CancellationToken.None);
        Assert.Equal(SyncLockStatus.Contended, contended.Status);
        await using var other = await provider.TryAcquireAsync(otherAccountId, SyncLockPurpose.AccountSync, CancellationToken.None);
        Assert.Equal(SyncLockStatus.Acquired, other.Status);
        await using var refresh = await provider.TryAcquireAsync(accountId, SyncLockPurpose.OAuthRefresh, CancellationToken.None);
        Assert.Equal(SyncLockStatus.Acquired, refresh.Status);
    }

    [Fact]
    public async Task SyncLock_ReleasedAfterDispose_AllowsReacquire()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var provider = new PostgresSyncLockProvider(fixture.ConnectionString, NullLogger<PostgresSyncLockProvider>.Instance);

        await using (var first = await provider.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, CancellationToken.None))
        {
            Assert.Equal(SyncLockStatus.Acquired, first.Status);
        }

        await using var second = await provider.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, CancellationToken.None);
        Assert.Equal(SyncLockStatus.Acquired, second.Status);
    }

    [Fact]
    public async Task SyncLock_CancelledWait_ReleasesLock()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var provider = new PostgresSyncLockProvider(fixture.ConnectionString, NullLogger<PostgresSyncLockProvider>.Instance);

        await using var first = await provider.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, CancellationToken.None);
        Assert.Equal(SyncLockStatus.Acquired, first.Status);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TryAcquireAsync(accountId, SyncLockPurpose.AccountSync, cancelled.Token));
    }

    [Fact]
    public async Task OAuthRefresh_SecondResolverWaitsForPostgresLockAndReusesPersistedToken()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedOAuthAccountAsync("old-access", "rotated-refresh", DateTime.UtcNow.AddMinutes(-10));
        var refreshCalls = new int[1];
        var firstRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshedToken = new OAuthToken("new-access", "rotated-refresh", DateTime.UtcNow.AddHours(1), "scope");
        var locks = new PostgresSyncLockProvider(fixture.ConnectionString, NullLogger<PostgresSyncLockProvider>.Instance);

        await using var firstDb = fixture.CreateDb();
        await using var secondDb = fixture.CreateDb();
        var first = TestServices.Credentials(firstDb,
            oauthProviders: [new BlockingOAuthProvider(refreshCalls, refreshedToken, firstRefreshStarted, allowFirstRefresh)], locks: locks);
        var second = TestServices.Credentials(secondDb,
            oauthProviders: [new CountingOAuthProvider(refreshCalls, refreshedToken, secondRefreshStarted)], locks: locks);

        var firstResolving = first.ResolveAsync(accountId, CancellationToken.None);
        await firstRefreshStarted.Task;
        var secondResolving = second.ResolveAsync(accountId, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondRefreshStarted.Task.WaitAsync(timeout.Token));
        Assert.Equal(1, refreshCalls[0]);
        allowFirstRefresh.SetResult();

        var resolved = await Task.WhenAll(firstResolving, secondResolving);

        Assert.All(resolved, credential => Assert.Equal("new-access", credential.Secret));
        Assert.Equal(1, refreshCalls[0]);
        await using var check = fixture.CreateDb();
        var stored = await check.MailCredentials.SingleAsync(credential => credential.MailAccountId == accountId);
        Assert.Contains("new-access", new PassthroughProtector().Unprotect(stored.EncryptedMaterial));
    }

    private async Task<Guid> SeedAccountAsync()
    {
        var account = Account($"seed-{Guid.NewGuid():N}@example.test");
        await using var db = fixture.CreateDb();
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedOAuthAccountAsync(string accessToken, string refreshToken, DateTime expiresAt)
    {
        var email = $"oauth-{Guid.NewGuid():N}@example.test";
        var account = Account(email);
        account.Provider = MailProvider.Google;
        account.AuthenticationMethod = AuthenticationMethod.OAuth2;
        var material = System.Text.Json.JsonSerializer.Serialize(new OAuthCredentialMaterial(accessToken, refreshToken));
        await using var db = fixture.CreateDb();
        db.MailAccounts.Add(account);
        db.MailCredentials.Add(new MailCredential
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            AuthenticationMethod = AuthenticationMethod.OAuth2,
            Provider = MailProvider.Google,
            EncryptedMaterial = new PassthroughProtector().Protect(material),
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private sealed class CountingOAuthProvider(int[] calls, OAuthToken token, TaskCompletionSource refreshStarted) : IOAuthProvider
    {
        public MailProvider Provider => MailProvider.Google;
        public bool IsConfigured => true;
        public string PrimaryRedirectUri => "https://app.example.test/oauth/callback";
        public string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge) => redirectUri;
        public Task<OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) => Task.FromResult(token);
        public Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls[0]);
            refreshStarted.SetResult();
            return Task.FromResult(token);
        }
    }

    private sealed class BlockingOAuthProvider(
        int[] calls,
        OAuthToken token,
        TaskCompletionSource refreshStarted,
        TaskCompletionSource allowRefresh) : IOAuthProvider
    {
        public MailProvider Provider => MailProvider.Google;
        public bool IsConfigured => true;
        public string PrimaryRedirectUri => "https://app.example.test/oauth/callback";
        public string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge) => redirectUri;
        public Task<OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) => Task.FromResult(token);
        public async Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls[0]);
            refreshStarted.SetResult();
            await allowRefresh.Task.WaitAsync(cancellationToken);
            return token;
        }
    }

    [Fact]
    public async Task ConversationEndpoints_TranslateOnPostgres_WithDistinctSendersAndOwnMessages()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var conversationId = Guid.NewGuid();
        await using (var db = fixture.CreateDb())
        {
            var folderId = await SeedFolderAsync(db, accountId);
            var ownAddress = await db.MailAccounts.Where(account => account.Id == accountId).Select(account => account.EmailAddress).SingleAsync();
            db.Conversations.Add(new Conversation { Id = conversationId, MailAccountId = accountId, NormalizedSubject = "pg thread", StartedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
            foreach (var (uid, from, name) in new[] { (401u, "alice@example.test", "Alice"), (402u, "alice@example.test", "Alice"), (403u, ownAddress.ToUpperInvariant(), "Me") })
            {
                var mail = Mail(accountId, folderId, uid);
                mail.ConversationId = conversationId;
                mail.FromAddress = from;
                mail.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = mail.Id, Type = ParticipantType.From, Address = from, NormalizedAddress = from.ToLowerInvariant(), DisplayName = name });
                db.Mails.Add(mail);
            }
            await db.SaveChangesAsync();
        }

        using var factory = new NpgsqlApiFactory(fixture.ConnectionString);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<MailClient.Application.Accounts.IJwtTokenIssuer>().Issue(accountId).Token);

        var list = await client.GetFromJsonAsync<System.Text.Json.JsonDocument>("/api/conversations");
        var detail = await client.GetFromJsonAsync<System.Text.Json.JsonDocument>($"/api/conversations/{conversationId}");

        Assert.Equal(["Alice", "Me"], list!.RootElement.GetProperty("items")[0].GetProperty("participants").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal([false, false, true], detail!.RootElement.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("isFromMe").GetBoolean()));
    }

    [Fact]
    public async Task AccountSyncStatusEndpoint_ReturnsPerFolderState_ScopedToOwnAccount()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        var otherAccountId = await SeedAccountAsync();
        await using (var db = fixture.CreateDb())
        {
            var folderId = await SeedFolderAsync(db, accountId);
            var otherFolderId = await SeedFolderAsync(db, otherAccountId);
            db.SyncStates.Add(new SyncState
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                MailFolderId = folderId,
                BackfillNextUid = 0,
                LastSuccessfulSyncAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                ConsecutiveFailures = 0
            });
            db.SyncStates.Add(new SyncState
            {
                Id = Guid.NewGuid(),
                MailAccountId = otherAccountId,
                MailFolderId = otherFolderId,
                BackfillNextUid = 500,
                LastFailureAt = DateTime.UtcNow,
                LastFailureCategory = SyncFailureCategory.Transient,
                ConsecutiveFailures = 2
            });
            await db.SaveChangesAsync();
        }

        using var factory = new NpgsqlApiFactory(fixture.ConnectionString);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<MailClient.Application.Accounts.IJwtTokenIssuer>().Issue(accountId).Token);

        var response = await client.GetFromJsonAsync<System.Text.Json.JsonDocument>("/api/account/sync-status");
        var entry = Assert.Single(response!.RootElement.EnumerateArray());

        // Only this account's folder comes back - the other account's failing/backfilling row is invisible.
        Assert.True(entry.GetProperty("backfillComplete").GetBoolean());
        Assert.Equal(0, entry.GetProperty("consecutiveFailures").GetInt32());
        Assert.True(entry.TryGetProperty("lastSuccessfulSyncAt", out var lastSync) && lastSync.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task SnoozeWakeup_SkipsRescheduledSnooze_AndConcurrentPassesNotifyOnce()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        var accountId = await SeedAccountAsync();
        MailSnooze rescheduled, due;
        await using (var db = fixture.CreateDb())
        {
            var folderId = await SeedFolderAsync(db, accountId);
            var rescheduledMail = Mail(accountId, folderId, 901);
            var dueMail = Mail(accountId, folderId, 902);
            db.Mails.AddRange(rescheduledMail, dueMail);
            rescheduled = new MailSnooze { Id = Guid.NewGuid(), MailAccountId = accountId, MailId = rescheduledMail.Id, UntilUtc = DateTime.UtcNow.AddMinutes(-5) };
            due = new MailSnooze { Id = Guid.NewGuid(), MailAccountId = accountId, MailId = dueMail.Id, UntilUtc = DateTime.UtcNow.AddMinutes(-1) };
            db.MailSnoozes.AddRange(rescheduled, due);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDb())
        {
            var stale = await db.MailSnoozes.AsNoTracking().Where(x => x.Id == rescheduled.Id).Select(x => x.UntilUtc).SingleAsync();
            await db.MailSnoozes.Where(x => x.Id == rescheduled.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UntilUtc, DateTime.UtcNow.AddHours(1)));
            Assert.False(await SnoozeWakeupService.ClaimAsync(db, rescheduled.Id, stale, CancellationToken.None));
        }

        var push = new FakePushNotificationService();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunSnoozeWakeupAsync(push)));

        var woken = Assert.Single(push.Notifications);
        Assert.Equal(PushEventType.SnoozeExpired, woken.Type);
        Assert.Equal(due.MailId, woken.MailId);
        await using var check = fixture.CreateDb();
        Assert.Equal([rescheduled.Id], await check.MailSnoozes.Where(x => x.MailAccountId == accountId).Select(x => x.Id).ToListAsync());
    }

    private async Task RunSnoozeWakeupAsync(IPushNotificationService push)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddSingleton(push);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await SnoozeWakeupService.ProcessDueAsync(scope.ServiceProvider, CancellationToken.None);
    }

    private static async Task<Guid> SeedFolderAsync(AppDbContext db, Guid accountId)
    {
        var folder = new MailFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsSyncEnabled = true,
            IsAvailable = true
        };
        db.MailFolders.Add(folder);
        await db.SaveChangesAsync();
        return folder.Id;
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
