using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.OAuth;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Tests;

public sealed class OAuthCredentialResolverTests
{
    [Fact]
    public async Task ResolveAsync_OAuthNearExpiry_RefreshesAndPersistsRotatedTokenEncrypted()
    {
        var db = Db();
        var accountId = await SeedOAuthAsync(db, DateTime.UtcNow.AddMinutes(1), "old-access", "old-refresh");
        var provider = new FakeOAuthProvider(new OAuthToken("new-access", "new-refresh", DateTime.UtcNow.AddHours(1), "scope"));
        var resolver = new MailCredentialResolver(db, new PrefixProtector(), new[] { provider });

        var resolved = await resolver.ResolveAsync(accountId, CancellationToken.None);

        Assert.Equal("new-access", resolved.Secret);
        Assert.Equal(AuthenticationMethod.OAuth2, resolved.AuthenticationMethod);
        var credential = await db.MailCredentials.SingleAsync(x => x.MailAccountId == accountId);
        Assert.StartsWith("protected:", credential.EncryptedMaterial, StringComparison.Ordinal);
        Assert.DoesNotContain("new-refresh", credential.EncryptedMaterial, StringComparison.Ordinal);
        var stored = JsonSerializer.Deserialize<OAuthCredentialMaterial>(new PrefixProtector().Unprotect(credential.EncryptedMaterial));
        Assert.Equal("new-refresh", stored!.RefreshToken);
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task ResolveAsync_InvalidRefresh_MarksAccountNeedsReauthentication()
    {
        var db = Db();
        var accountId = await SeedOAuthAsync(db, DateTime.UtcNow.AddMinutes(1), "old-access", "old-refresh");
        var resolver = new MailCredentialResolver(db, new PrefixProtector(), new[] { FakeOAuthProvider.InvalidGrant() });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(accountId, CancellationToken.None));

        Assert.Equal("mail_account_needs_reauthentication", error.Message);
        Assert.Equal(MailAccountStatus.NeedsReauthentication, (await db.MailAccounts.SingleAsync(x => x.Id == accountId)).Status);
    }

    [Fact]
    public async Task ResolveAsync_Refresh_RecordsSuccessFailureAndReauthenticationMetrics()
    {
        using var capture = new MetricsCapture();
        var db = Db();
        var success = await SeedOAuthAsync(db, DateTime.UtcNow.AddMinutes(1), "old-access", "old-refresh");
        await Resolver(db, new FakeOAuthProvider(new OAuthToken("new-access", null, DateTime.UtcNow.AddHours(1), "scope")), capture)
            .ResolveAsync(success, CancellationToken.None);

        var reauth = await SeedOAuthAsync(db, DateTime.UtcNow.AddMinutes(1), "old-access", "old-refresh");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver(db, FakeOAuthProvider.InvalidGrant(), capture).ResolveAsync(reauth, CancellationToken.None));

        var failure = await SeedOAuthAsync(db, DateTime.UtcNow.AddMinutes(1), "old-access", "old-refresh");
        await Assert.ThrowsAsync<HttpRequestException>(() => Resolver(db, FakeOAuthProvider.Unreachable(), capture).ResolveAsync(failure, CancellationToken.None));

        Assert.Equal(1, capture.Sum("mailclient.oauth.token.refresh", ("provider", "google"), ("result", "success")));
        Assert.Equal(1, capture.Sum("mailclient.oauth.token.refresh", ("provider", "google"), ("result", "reauthentication_required")));
        Assert.Equal(1, capture.Sum("mailclient.oauth.token.refresh", ("provider", "google"), ("result", "failure")));
        Assert.Equal(1, capture.Sum("mailclient.oauth.reauthentication.required", ("provider", "google")));
        Assert.Equal(3, capture.For("mailclient.oauth.token.refresh.duration").Count);
        Assert.DoesNotContain(capture.All.SelectMany(item => item.Tags.Values), value => Convert.ToString(value)!.Contains("refresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_LockInfrastructureFailure_FailsWithoutUnlockedRefresh()
    {
        var db = Db();
        var accountId = await SeedOAuthAsync(db, DateTime.UtcNow.AddMinutes(1), "old-access", "old-refresh");
        var provider = new FakeOAuthProvider(new OAuthToken("new-access", "new-refresh", DateTime.UtcNow.AddHours(1), "scope"));
        var locks = new StubSyncLockProvider(SyncLockStatus.InfrastructureFailure);
        var resolver = new MailCredentialResolver(db, new PrefixProtector(), [provider], new DefaultRuntimePolicyProvider(), null, null, locks);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(accountId, CancellationToken.None));

        Assert.Equal(SyncFailureClassifier.OAuthRefreshLockUnavailable, error.Message);
        Assert.Equal(SyncFailureCategory.Transient, SyncFailureClassifier.Classify(error));
        Assert.Equal(0, provider.RefreshCount);
        Assert.Equal(1, locks.Calls);
        Assert.Equal(MailAccountStatus.Active, (await db.MailAccounts.SingleAsync(x => x.Id == accountId)).Status);
    }

    [Fact]
    public async Task ResolveAsync_LockContention_WaitsForOwnershipBeforeRefreshing()
    {
        var db = Db();
        var accountId = await SeedOAuthAsync(db, DateTime.UtcNow.AddMinutes(1), "old-access", "old-refresh");
        var provider = new FakeOAuthProvider(new OAuthToken("new-access", "new-refresh", DateTime.UtcNow.AddHours(1), "scope"));
        var locks = new StubSyncLockProvider(SyncLockStatus.Contended, SyncLockStatus.Acquired);
        var resolver = new MailCredentialResolver(db, new PrefixProtector(), [provider], new DefaultRuntimePolicyProvider(), null, null, locks);

        var resolved = await resolver.ResolveAsync(accountId, CancellationToken.None);

        Assert.Equal("new-access", resolved.Secret);
        Assert.Equal(2, locks.Calls);
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task ResolveAsync_Refresh_KeepsCallerTrackedChangesPersistable()
    {
        var db = Db();
        var accountId = await SeedOAuthAsync(db, DateTime.UtcNow.AddMinutes(1), "old-access", "old-refresh");
        var account = await db.MailAccounts.SingleAsync(x => x.Id == accountId);
        var provider = new FakeOAuthProvider(new OAuthToken("new-access", "new-refresh", DateTime.UtcNow.AddHours(1), "scope"));
        var resolver = new MailCredentialResolver(db, new PrefixProtector(), [provider], new DefaultRuntimePolicyProvider(), null, null,
            new StubSyncLockProvider(SyncLockStatus.Acquired));

        await resolver.ResolveAsync(accountId, CancellationToken.None);
        account.DisplayName = "changed after refresh";
        await db.SaveChangesAsync();

        Assert.Equal("changed after refresh", (await db.MailAccounts.AsNoTracking().SingleAsync(x => x.Id == accountId)).DisplayName);
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task ResolveAsync_PasswordAccount_ReturnsPasswordWithoutOAuthRefresh()
    {
        var db = Db();
        var accountId = await SeedPasswordAsync(db);
        var provider = new FakeOAuthProvider(new OAuthToken("new-access", "new-refresh", DateTime.UtcNow.AddHours(1), "scope"));
        var resolver = new MailCredentialResolver(db, new PrefixProtector(), new[] { provider });

        var resolved = await resolver.ResolveAsync(accountId, CancellationToken.None);

        Assert.Equal("password-secret", resolved.Secret);
        Assert.Equal(AuthenticationMethod.Password, resolved.AuthenticationMethod);
        Assert.Equal(0, provider.RefreshCount);
    }

    private static async Task<Guid> SeedPasswordAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "person@example.test",
            NormalizedEmailAddress = "PERSON@EXAMPLE.TEST",
            Username = "person@example.test",
            Provider = MailProvider.Custom,
            AuthenticationMethod = AuthenticationMethod.Password,
            Status = MailAccountStatus.Active
        });
        db.MailCredentials.Add(new MailCredential
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            AuthenticationMethod = AuthenticationMethod.Password,
            Provider = MailProvider.Custom,
            EncryptedMaterial = new PrefixProtector().Protect("password-secret"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return accountId;
    }

    private static async Task<Guid> SeedOAuthAsync(AppDbContext db, DateTime expiresAt, string accessToken, string refreshToken)
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
            EncryptedMaterial = new PrefixProtector().Protect(JsonSerializer.Serialize(new OAuthCredentialMaterial(accessToken, refreshToken))),
            ExpiresAt = expiresAt,
            Scopes = "scope",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return accountId;
    }

    private static MailCredentialResolver Resolver(AppDbContext db, IOAuthProvider provider, MetricsCapture capture) =>
        new(db, new PrefixProtector(), [provider], new DefaultRuntimePolicyProvider(), null, null, null, capture.Metrics);

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private sealed class PrefixProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => "protected:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["protected:".Length..]));
    }

    private sealed class FakeOAuthProvider(OAuthToken token) : IOAuthProvider
    {
        private readonly Exception? _failure;
        private FakeOAuthProvider(Exception failure) : this(new OAuthToken("", null, DateTime.UtcNow, "")) => _failure = failure;
        public MailProvider Provider => MailProvider.Google;
        public bool IsConfigured => true;
        public string PrimaryRedirectUri => "app://oauth";
        public int RefreshCount { get; private set; }
        public string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge) => throw new NotSupportedException();
        public Task<OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            RefreshCount++;
            if (_failure is not null)
                throw _failure;
            return Task.FromResult(token);
        }
        public static FakeOAuthProvider InvalidGrant() => new(new InvalidOperationException("mail_account_needs_reauthentication"));
        public static FakeOAuthProvider Unreachable() => new(new HttpRequestException("token endpoint unavailable"));
    }
}
