using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.OAuth;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class OAuthApiTests
{
    [Fact]
    public async Task StartGoogle_Configured_ReturnsUrlWithScopesAndState()
    {
        using var factory = new OAuthAcceptingApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/accounts/oauth/google/start", new { email = "user@gmail.com" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var authUrl = body.GetProperty("authorizationUrl").GetString()!;
        var state = body.GetProperty("state").GetString()!;

        Assert.Contains("mail.google.com", authUrl);
        Assert.Contains("code_challenge", authUrl);
        Assert.Contains("code_challenge_method=S256", authUrl);
        Assert.False(string.IsNullOrWhiteSpace(state));
    }

    [Fact]
    public async Task StartMicrosoft_Configured_ReturnsUrlWithScopesAndState()
    {
        using var factory = new OAuthAcceptingApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/accounts/oauth/microsoft/start", new { email = "user@outlook.com" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var authUrl = body.GetProperty("authorizationUrl").GetString()!;

        Assert.Contains("login.microsoftonline.com", authUrl);
        Assert.Contains("IMAP.AccessAsUser.All", authUrl);
        Assert.Contains("SMTP.Send", authUrl);
        Assert.Contains("offline_access", authUrl);
    }

    [Theory]
    [InlineData("missing-at-sign")]
    [InlineData("victim@example.com\r\nattacker@example.com")]
    [InlineData("victim@example.com attacker@example.com")]
    public async Task StartGoogle_InvalidEmail_400(string email)
    {
        using var factory = new OAuthAcceptingApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/accounts/oauth/google/start", new { email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_email", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task StartGoogle_NotConfigured_422()
    {
        using var factory = new AcceptingApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/accounts/oauth/google/start", new { email = "user@gmail.com" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("oauth_provider_not_configured", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task StartUnknownProvider_422()
    {
        using var factory = new OAuthAcceptingApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/accounts/oauth/yahoo/start", new { email = "user@yahoo.com" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CompleteSuccess_ReturnsAppTokensWithoutProviderTokens()
    {
        const string providerAccess = "test-access-token";
        const string providerRefresh = "test-refresh-token";

        using var factory = new OAuthAcceptingApiFactory(providerAccess, providerRefresh);
        using var client = factory.CreateClient();

        var startResp = await client.PostAsJsonAsync("/api/accounts/oauth/google/start", new { email = "user@gmail.com" });
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var startBody = await startResp.Content.ReadFromJsonAsync<JsonElement>();
        var state = startBody.GetProperty("state").GetString()!;

        var response = await client.PostAsJsonAsync("/api/accounts/oauth/google/complete", new { state, code = "auth-code" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString()!;
        var refreshToken = body.GetProperty("refreshToken").GetString()!;
        var mailAccountId = body.GetProperty("mailAccountId").GetGuid();

        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshToken));
        Assert.NotEqual(providerAccess, accessToken);
        Assert.NotEqual(providerRefresh, refreshToken);

        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(providerAccess, rawJson);
        Assert.DoesNotContain(providerRefresh, rawJson);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.MailAccounts.Include(x => x.Credentials).SingleAsync(x => x.Id == mailAccountId);
        Assert.Equal(Domain.Enums.AuthenticationMethod.OAuth2, account.AuthenticationMethod);
        var credential = Assert.Single(account.Credentials);
        Assert.DoesNotContain(providerAccess, credential.EncryptedMaterial);
        Assert.DoesNotContain(providerRefresh, credential.EncryptedMaterial);
    }

    [Fact]
    public async Task CompleteValidationFails_NoAccountPersisted()
    {
        using var factory = new OAuthRejectingApiFactory();
        using var client = factory.CreateClient();

        var startResp = await client.PostAsJsonAsync("/api/accounts/oauth/google/start", new { email = "user@gmail.com" });
        var startBody = await startResp.Content.ReadFromJsonAsync<JsonElement>();
        var state = startBody.GetProperty("state").GetString()!;

        var response = await client.PostAsJsonAsync("/api/accounts/oauth/google/complete", new { state, code = "auth-code" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.MailAccounts.CountAsync());
    }

    [Fact]
    public async Task CompleteTamperedState_422()
    {
        using var factory = new OAuthAcceptingApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/accounts/oauth/google/complete", new { state = "garbage-tampered-state", code = "auth-code" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("oauth_state_invalid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExistingPasswordAccounts_UnaffectedByOAuthGating()
    {
        using var factory = new OAuthAcceptingApiFactory();
        using var client = factory.CreateClient();

        var connect = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid"));
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
    }
}

internal sealed class FakeGoogleOAuthProvider(string access = "test-access-token", string refresh = "test-refresh-token") : IOAuthProvider
{
    public MailProvider Provider => MailProvider.Google;
    public bool IsConfigured => true;
    public string PrimaryRedirectUri => "app://oauth";
    public string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge) =>
        $"https://accounts.google.com/o/oauth2/v2/auth?client_id=test&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=https%3A%2F%2Fmail.google.com%2F&state={Uri.EscapeDataString(state)}&code_challenge={codeChallenge}&code_challenge_method=S256&access_type=offline&prompt=consent&login_hint={Uri.EscapeDataString(email)}";
    public Task<OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult(new OAuthToken(access, refresh, DateTime.UtcNow.AddHours(1), "https://mail.google.com/"));
    public Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(new OAuthToken(access, "new-" + refresh, DateTime.UtcNow.AddHours(1), "https://mail.google.com/"));
}

internal sealed class FakeMicrosoftOAuthProvider : IOAuthProvider
{
    public MailProvider Provider => MailProvider.Microsoft;
    public bool IsConfigured => true;
    public string PrimaryRedirectUri => "app://oauth";
    public string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge) =>
        $"https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id=test&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=https%3A%2F%2Foutlook.office.com%2FIMAP.AccessAsUser.All+https%3A%2F%2Foutlook.office.com%2FSMTP.Send+offline_access&state={Uri.EscapeDataString(state)}&code_challenge={codeChallenge}&code_challenge_method=S256&login_hint={Uri.EscapeDataString(email)}";
    public Task<OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult(new OAuthToken("ms-access", "ms-refresh", DateTime.UtcNow.AddHours(1), "scope"));
    public Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(new OAuthToken("ms-access", "ms-new-refresh", DateTime.UtcNow.AddHours(1), "scope"));
}

public sealed class OAuthAcceptingApiFactory(string providerAccess = "test-access-token", string providerRefresh = "test-refresh-token") : MailClientApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IOAuthProvider>(new FakeGoogleOAuthProvider(providerAccess, providerRefresh));
            services.AddSingleton<IOAuthProvider, FakeMicrosoftOAuthProvider>();
        });
    }
}

public sealed class OAuthRejectingApiFactory : MailClientApiFactory
{
    protected override bool AcceptCandidates => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IOAuthProvider>(new FakeGoogleOAuthProvider());
            services.AddSingleton<IOAuthProvider, FakeMicrosoftOAuthProvider>();
        });
    }
}
