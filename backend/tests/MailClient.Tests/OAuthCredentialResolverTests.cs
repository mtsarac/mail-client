using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.OAuth;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
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
        private readonly bool _invalidGrant;
        private FakeOAuthProvider() : this(new OAuthToken("", null, DateTime.UtcNow, "")) => _invalidGrant = true;
        public MailProvider Provider => MailProvider.Google;
        public bool IsConfigured => true;
        public string PrimaryRedirectUri => "app://oauth";
        public int RefreshCount { get; private set; }
        public string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge) => throw new NotSupportedException();
        public Task<OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            RefreshCount++;
            if (_invalidGrant)
                throw new InvalidOperationException("mail_account_needs_reauthentication");
            return Task.FromResult(token);
        }
        public static FakeOAuthProvider InvalidGrant() => new();
    }
}
