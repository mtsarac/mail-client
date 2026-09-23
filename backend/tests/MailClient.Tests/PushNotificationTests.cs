using System.Text.Json;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Push;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static MailClient.Tests.SyncTestSeed;

namespace MailClient.Tests;

public sealed class PushNotificationTests
{
    [Fact]
    public async Task NotifyAsync_InvalidToken_RemovesOnlyOwningAccountToken()
    {
        var dbName = DbName();
        var accountA = await SeedDeviceAsync(dbName, "token-a");
        var accountB = await SeedDeviceAsync(dbName, "token-b");
        var gateway = new FakeFirebaseGateway { ResultFor = recipient => new FirebaseSendResult(recipient.DbId, false, true) };
        var service = CreatePushService(dbName, gateway, new RuntimeSettings());

        await service.NotifyAsync(new PushEvent(PushEventType.NewMail, accountA, Guid.NewGuid(), null, Guid.NewGuid()), CancellationToken.None);

        await using var check = CreateDb(dbName);
        Assert.Equal(0, await check.DeviceTokens.CountAsync(x => x.MailAccountId == accountA));
        Assert.Equal(1, await check.DeviceTokens.CountAsync(x => x.MailAccountId == accountB));
    }

    [Fact]
    public async Task NotifyAsync_InvalidToken_RecordsInvalidTokenMetricWithoutDeviceToken()
    {
        using var capture = new MetricsCapture();
        var dbName = DbName();
        var accountId = await SeedDeviceAsync(dbName, "device-token-secret");
        var gateway = new FakeFirebaseGateway { ResultFor = recipient => new FirebaseSendResult(recipient.DbId, false, true) };
        var service = CreatePushService(dbName, gateway, new FakeRuntimeSettingsStore(new RuntimeSettings()), capture.Metrics);

        await service.NotifyAsync(new PushEvent(PushEventType.NewMail, accountId, Guid.NewGuid(), null, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(1, capture.Sum("mailclient.push.invalid_tokens", ("event_type", "new_mail")));
        Assert.Equal(1, capture.Sum("mailclient.push.notifications", ("event_type", "new_mail"), ("result", "failure")));
        Assert.Single(capture.For("mailclient.push.duration"));
        Assert.DoesNotContain(capture.All.SelectMany(item => item.Tags.Values), value => Convert.ToString(value)!.Contains("device-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyAsync_TransientFailure_KeepsTokenWithoutThrowing()
    {
        var dbName = DbName();
        var accountId = await SeedDeviceAsync(dbName, "token-a");
        var gateway = new FakeFirebaseGateway { Throw = new HttpRequestException("fcm down") };
        var service = CreatePushService(dbName, gateway, new RuntimeSettings());

        await service.NotifyAsync(new PushEvent(PushEventType.NewMail, accountId, Guid.NewGuid(), null, Guid.NewGuid()), CancellationToken.None);

        await using var check = CreateDb(dbName);
        Assert.Equal(1, await check.DeviceTokens.CountAsync(x => x.MailAccountId == accountId));
        Assert.Single(gateway.Calls);
    }

    [Fact]
    public async Task NotifyAsync_NewMail_PayloadContainsOnlyExpectedFields()
    {
        var dbName = DbName();
        var accountId = await SeedDeviceAsync(dbName, "token-a");
        var mailId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var gateway = new FakeFirebaseGateway();
        var service = CreatePushService(dbName, gateway, new RuntimeSettings());

        await service.NotifyAsync(
            new PushEvent(PushEventType.NewMail, accountId, mailId, conversationId, folderId,
                SenderPreview: "sender@example.test", SubjectPreview: "hello"),
            CancellationToken.None);

        var call = Assert.Single(gateway.Calls);
        Assert.Equal(["accountId", "conversationId", "folderId", "mailId", "type"], call.Data.Keys.Order().ToArray());
        Assert.Equal("new_mail", call.Data["type"]);
        Assert.Equal(accountId.ToString(), call.Data["accountId"]);
        Assert.Equal(mailId.ToString(), call.Data["mailId"]);
        Assert.Equal(conversationId.ToString(), call.Data["conversationId"]);
        Assert.Equal(folderId.ToString(), call.Data["folderId"]);
        Assert.Equal("sender@example.test", call.Title);
        Assert.Equal("hello", call.Body);
    }

    [Fact]
    public async Task NotifyAsync_PreviewDisabled_SendsGenericTextWithoutSenderOrSubject()
    {
        var dbName = DbName();
        var accountId = await SeedDeviceAsync(dbName, "token-a");
        var gateway = new FakeFirebaseGateway();
        var settings = new RuntimeSettings { Push = new RuntimePushSettings { IncludeMailPreview = false } };
        var service = CreatePushService(dbName, gateway, settings);

        await service.NotifyAsync(
            new PushEvent(PushEventType.NewMail, accountId, Guid.NewGuid(), null, Guid.NewGuid(),
                SenderPreview: "secret-sender@example.test", SubjectPreview: "secret-subject"),
            CancellationToken.None);

        var call = Assert.Single(gateway.Calls);
        Assert.Equal("New mail", call.Title);
        Assert.Equal("You have a new message.", call.Body);
        Assert.DoesNotContain("secret-sender@example.test", call.Title + call.Body + string.Join(" ", call.Data.Values));
        Assert.DoesNotContain("secret-subject", call.Title + call.Body + string.Join(" ", call.Data.Values));
    }

    [Fact]
    public async Task NotifyAsync_PushDisabledTakesEffectWithoutRestart()
    {
        var dbName = DbName();
        var accountId = await SeedDeviceAsync(dbName, "token-a");
        var gateway = new FakeFirebaseGateway();
        var store = new FakeRuntimeSettingsStore(new RuntimeSettings());
        var service = CreatePushService(dbName, gateway, store);

        await service.NotifyAsync(new PushEvent(PushEventType.NewMail, accountId, Guid.NewGuid(), null, Guid.NewGuid()), CancellationToken.None);
        Assert.Single(gateway.Calls);

        store.Current = new RuntimeSettings { Push = new RuntimePushSettings { Enabled = false } };
        await service.NotifyAsync(new PushEvent(PushEventType.NewMail, accountId, Guid.NewGuid(), null, Guid.NewGuid()), CancellationToken.None);
        Assert.Single(gateway.Calls);
    }

    [Fact]
    public async Task NotifyAsync_EventFlagDisabled_SkipsOnlyThatEventType()
    {
        var dbName = DbName();
        var accountId = await SeedDeviceAsync(dbName, "token-a");
        var gateway = new FakeFirebaseGateway();
        var settings = new RuntimeSettings { Push = new RuntimePushSettings { NewMailEnabled = false } };
        var service = CreatePushService(dbName, gateway, settings);

        await service.NotifyAsync(new PushEvent(PushEventType.NewMail, accountId, Guid.NewGuid(), null, Guid.NewGuid()), CancellationToken.None);
        Assert.Empty(gateway.Calls);

        await service.NotifyAsync(new PushEvent(PushEventType.AccountReauthenticationRequired, accountId), CancellationToken.None);
        var call = Assert.Single(gateway.Calls);
        Assert.Equal("account_reauthentication_required", call.Data["type"]);
    }

    [Fact]
    public async Task NotifyAsync_StateChanged_IsDataOnly()
    {
        var dbName = DbName();
        var accountId = await SeedDeviceAsync(dbName, "token-a");
        var gateway = new FakeFirebaseGateway();
        var service = CreatePushService(dbName, gateway, new RuntimeSettings());

        await service.NotifyAsync(
            new PushEvent(PushEventType.MailStateChanged, accountId, Guid.NewGuid(), null, Guid.NewGuid(), "star"),
            CancellationToken.None);

        var call = Assert.Single(gateway.Calls);
        Assert.Null(call.Title);
        Assert.Null(call.Body);
        Assert.Equal("mail_state_changed", call.Data["type"]);
        Assert.Equal("star", call.Data["operation"]);
    }

    [Fact]
    public async Task SyncFolder_MissingCredential_EmitsSingleReauthenticationEvent()
    {
        await using var db = CreateDb(DbName());
        var (accountId, folderId) = await SeedFolderAsync(db);
        var push = new FakePushNotificationService();
        var service = CreateSyncService(db, push);

        await service.SyncFolderAsync(accountId, folderId, CancellationToken.None);
        await service.SyncFolderAsync(accountId, folderId, CancellationToken.None);

        var reauth = Assert.Single(push.Notifications);
        Assert.Equal(PushEventType.AccountReauthenticationRequired, reauth.Type);
        Assert.Equal(accountId, reauth.MailAccountId);
        Assert.Equal(MailAccountStatus.NeedsReauthentication,
            (await db.MailAccounts.SingleAsync(x => x.Id == accountId)).Status);
    }

    [Fact]
    public async Task SyncFolder_NewMail_IncludesConversationId()
    {
        await using var db = CreateDb(DbName());
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeKit.MimeMessage>>
        {
            [1] = () => SimpleMessage("threaded")
        });
        var push = new FakePushNotificationService();
        var service = CreateSyncService(db, push);

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var notification = Assert.Single(push.Notifications);
        Assert.Equal(PushEventType.NewMail, notification.Type);
        var stored = await db.Mails.SingleAsync();
        Assert.NotNull(stored.ConversationId);
        Assert.Equal(stored.ConversationId, notification.ConversationId);
        Assert.Equal(stored.Id, notification.MailId);
    }

    [Fact]
    public async Task Operation_Star_EmitsStateChangedEvent()
    {
        await using var db = CreateDb(DbName());
        var (accountId, _, mailId) = await SeedMailAsync(db);
        var push = new FakePushNotificationService();
        var service = CreateOperationService(db, new FakeMailFolderClient(new FakeRemoteMailFolder(7, new())), push);

        var result = await service.ExecuteAsync(accountId, new MailOperationRequest(mailId, MailOperationKind.Star), null, CancellationToken.None);

        Assert.True(result.Success);
        var notification = Assert.Single(push.Notifications);
        Assert.Equal(PushEventType.MailStateChanged, notification.Type);
        Assert.Equal(accountId, notification.MailAccountId);
        Assert.Equal(mailId, notification.MailId);
        Assert.Equal("star", notification.Operation);
    }

    [Fact]
    public async Task Operation_PushFailure_StillSucceeds()
    {
        await using var db = CreateDb(DbName());
        var (accountId, _, mailId) = await SeedMailAsync(db);
        var service = CreateOperationService(db, new FakeMailFolderClient(new FakeRemoteMailFolder(7, new())), new ThrowingPush());

        var result = await service.ExecuteAsync(accountId, new MailOperationRequest(mailId, MailOperationKind.Star), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True((await db.Mails.SingleAsync(x => x.Id == mailId)).Flagged);
    }

    [Fact]
    public async Task Sync_PushFailure_StillPersistsMail()
    {
        await using var db = CreateDb(DbName());
        var (accountId, folderId) = await SeedFolderAsync(db);
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeKit.MimeMessage>>
        {
            [1] = () => SimpleMessage("push-down")
        });
        var service = CreateSyncService(db, new ThrowingPush());

        await service.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        Assert.Equal("push-down", (await db.Mails.SingleAsync()).Subject);
    }

    [Fact]
    public async Task Resolver_InvalidGrant_EmitsSingleReauthenticationEvent()
    {
        await using var db = CreateDb(DbName());
        var accountId = await SeedOAuthAsync(db);
        var push = new FakePushNotificationService();
        var resolver = TestServices.Credentials(db, oauthProviders: [FailingOAuthProvider.InvalidGrant()], push: push);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(accountId, CancellationToken.None));
        Assert.Equal("mail_account_needs_reauthentication", first.Message);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(accountId, CancellationToken.None));
        Assert.Equal("mail_account_needs_reauthentication", second.Message);

        var reauth = Assert.Single(push.Notifications);
        Assert.Equal(PushEventType.AccountReauthenticationRequired, reauth.Type);
        Assert.Equal(accountId, reauth.MailAccountId);
    }

    private static string DbName() => Guid.NewGuid().ToString("N");

    private static AppDbContext CreateDb(string name) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(name).Options);

    private static async Task<Guid> SeedDeviceAsync(string dbName, string token)
    {
        await using var db = CreateDb(dbName);
        var accountId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@example.test",
            NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
            Username = "a",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        db.DeviceTokens.Add(new DeviceToken
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Token = token,
            Platform = "android",
            RegisteredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return accountId;
    }

    private static FirebasePushNotificationService CreatePushService(string dbName, FakeFirebaseGateway gateway, RuntimeSettings settings) =>
        CreatePushService(dbName, gateway, new FakeRuntimeSettingsStore(settings));

    private static FirebasePushNotificationService CreatePushService(
        string dbName,
        FakeFirebaseGateway gateway,
        FakeRuntimeSettingsStore store,
        Application.Observability.MailClientMetrics? metrics = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IRuntimeSettingsStore>(store);
        services.AddSingleton<IFirebaseGateway>(gateway);
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return new FirebasePushNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            gateway,
            NullLogger<FirebasePushNotificationService>.Instance,
            metrics);
    }

    private static MailFolderSyncService CreateSyncService(AppDbContext db, IPushNotificationService push) =>
        new(db,
            TestServices.Credentials(db),
            new MailConnectionHelper(
                new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)),
                NullLogger<MailConnectionHelper>.Instance),
            new FakeFileStorage(),
            FixedRuntimeSettingsStore.Operation(TestServices.SyncSettings(flagSyncIntervalSeconds: 3600, maxAttachmentBytes: 500, maxMessageAttachmentBytes: 800, maxMessageBytes: 100000)),
            push,
            new ConversationService(db),
            new MailReconciliationService(db),
            NullLogger<MailFolderSyncService>.Instance);

    private static MailOperationService CreateOperationService(AppDbContext db, IMailFolderClient folders, IPushNotificationService push) =>
        new(db,
            folders,
            new MailReadService(db, folders, new AuditLogger(db), NullLogger<MailReadService>.Instance),
            new AuditLogger(db),
            new FakeSyncScheduler(),
            NullLogger<MailOperationService>.Instance,
            push);

    private static async Task<(Guid AccountId, Guid FolderId, Guid MailId)> SeedMailAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount { Id = accountId, EmailAddress = "a@example.test", NormalizedEmailAddress = "A@EXAMPLE.TEST", Username = "a", Status = MailAccountStatus.Active });
        db.MailFolders.Add(new MailFolder { Id = folderId, MailAccountId = accountId, Name = "INBOX", FullName = "INBOX", FolderType = MailFolderType.Inbox, UidValidity = 7 });
        db.Mails.Add(new Mail { Id = mailId, MailAccountId = accountId, MailFolderId = folderId, Uid = 5, UidValidity = 7, Subject = "test" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId, mailId);
    }

    private static async Task<Guid> SeedOAuthAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "person@gmail.com",
            NormalizedEmailAddress = "PERSON@GMAIL.COM",
            Username = "person@gmail.com",
            Provider = MailProvider.Google,
            AuthenticationMethod = AuthenticationMethod.OAuth2,
            Status = MailAccountStatus.Active
        });
        db.MailCredentials.Add(new MailCredential
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            AuthenticationMethod = AuthenticationMethod.OAuth2,
            Provider = MailProvider.Google,
            EncryptedMaterial = JsonSerializer.Serialize(new OAuthCredentialMaterial("old-access", "old-refresh")),
            ExpiresAt = DateTime.UtcNow.AddMinutes(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return accountId;
    }

    private sealed class FakeRuntimeSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Current { get; set; } = settings;
        public Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeSettingsSnapshot(Current, 1, DateTime.UtcNow));
        public Task<RuntimeSettingsSnapshot> ReplaceAsync(int expectedVersion, RuntimeSettings value, CancellationToken cancellationToken)
        {
            Current = value;
            return GetAsync(cancellationToken);
        }
    }

    private sealed class FakeFirebaseGateway : IFirebaseGateway
    {
        public List<(IReadOnlyList<FirebaseRecipient> Recipients, string? Title, string? Body, IReadOnlyDictionary<string, string> Data)> Calls { get; } = [];
        public Func<FirebaseRecipient, FirebaseSendResult>? ResultFor { get; set; }
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<FirebaseSendResult>> SendAsync(
            IReadOnlyList<FirebaseRecipient> recipients,
            string? title,
            string? body,
            IReadOnlyDictionary<string, string> data,
            CancellationToken cancellationToken)
        {
            Calls.Add((recipients, title, body, data));
            if (Throw is not null)
                throw Throw;
            return Task.FromResult<IReadOnlyList<FirebaseSendResult>>(
                recipients.Select(recipient => ResultFor?.Invoke(recipient) ?? new FirebaseSendResult(recipient.DbId, true, false)).ToList());
        }
    }

    private sealed class ThrowingPush : IPushNotificationService
    {
        public Task NotifyAsync(PushEvent pushEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("push down");
    }

    private sealed class FailingOAuthProvider : Infrastructure.OAuth.IOAuthProvider
    {
        private FailingOAuthProvider() { }
        public MailProvider Provider => MailProvider.Google;
        public bool IsConfigured => true;
        public string PrimaryRedirectUri => "app://oauth";
        public Task<Infrastructure.OAuth.OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Infrastructure.OAuth.OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("mail_account_needs_reauthentication");
        public string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge) => throw new NotSupportedException();
        public static FailingOAuthProvider InvalidGrant() => new();
    }
}
