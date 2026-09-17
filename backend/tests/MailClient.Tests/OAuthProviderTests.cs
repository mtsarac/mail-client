using System.Net;
using System.Text.Json;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.OAuth;
using Microsoft.AspNetCore.DataProtection;

namespace MailClient.Tests;

public sealed class OAuthProviderTests
{
    [Fact]
    public void GoogleAuthorization_UsesMailScopeOfflineAccessAndPkce()
    {
        var provider = new GoogleOAuthProvider(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            Config(MailProvider.Google));

        var uri = new Uri(provider.CreateAuthorizationUrl("person@gmail.com", "app://oauth", "opaque", "challenge"));
        var query = ParseQuery(uri);

        Assert.Equal("https://mail.google.com/", query["scope"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("challenge", query["code_challenge"]);
    }

    [Fact]
    public void MicrosoftAuthorization_UsesMailScopesOfflineAccessAndPkce()
    {
        var provider = new MicrosoftOAuthProvider(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            Config(MailProvider.Microsoft));

        var uri = new Uri(provider.CreateAuthorizationUrl("person@outlook.com", "app://oauth", "opaque", "challenge"));
        var query = ParseQuery(uri);

        Assert.Contains("https://outlook.office.com/IMAP.AccessAsUser.All", query["scope"]);
        Assert.Contains("https://outlook.office.com/SMTP.Send", query["scope"]);
        Assert.Contains("offline_access", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
    }

    [Fact]
    public async Task TokenExchange_MapsProviderResponseWithoutReturningRawBody()
    {
        const string json = """
            {"access_token":"access-secret","refresh_token":"refresh-secret","expires_in":3600,"scope":"mail offline_access","token_type":"Bearer"}
            """;
        var provider = new GoogleOAuthProvider(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            })),
            Config(MailProvider.Google));

        var token = await provider.ExchangeCodeAsync("code", "verifier", "app://oauth", CancellationToken.None);

        Assert.Equal("access-secret", token.AccessToken);
        Assert.Equal("refresh-secret", token.RefreshToken);
        Assert.Equal("mail offline_access", token.Scopes);
        Assert.True(token.ExpiresAt > DateTime.UtcNow.AddMinutes(50));
    }

    [Fact]
    public void ProtectedState_RejectsTamperingAndEnforcesLifetime()
    {
        var keys = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var provider = DataProtectionProvider.Create(keys);
            var states = new OAuthStateProtector(provider, TimeSpan.FromMinutes(10));
            var protectedState = states.Protect(new OAuthStatePayload(MailProvider.Google, "person@gmail.com", "app://oauth", "verifier", "device", "nonce"));

            var roundTrip = states.Consume(protectedState);
            Assert.Equal("verifier", roundTrip.CodeVerifier);
            Assert.Throws<InvalidOperationException>(() => states.Consume(protectedState));
            Assert.Throws<InvalidOperationException>(() => states.Consume(protectedState + "tampered"));
        }
        finally
        {
            if (keys.Exists)
                keys.Delete(true);
        }
    }

    private static OAuthProviderOptions Config(MailProvider provider) => new()
    {
        ClientId = "client-id",
        ClientSecret = "client-secret",
        Tenant = "organizations",
        RedirectUris = ["app://oauth"]
    };

    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1].Replace('+', ' ')));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
