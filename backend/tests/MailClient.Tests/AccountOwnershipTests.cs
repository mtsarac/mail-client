using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailClient.Api.Auth;
using MailClient.Application.Accounts;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace MailClient.Tests;

public sealed class AccountOwnershipTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task ConnectManual_ExistingEmail_IsRejectedAndLeavesTheAccountUntouched()
    {
        var email = $"victim-{Guid.NewGuid():N}@example.test";
        var client = factory.CreateClient();
        var owner = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", email));
        owner.EnsureSuccessStatusCode();
        var accountId = (await owner.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("mailAccountId").GetGuid();

        // Attacker nominates a mail server they control for an address that already has an account.
        var attacker = await factory.CreateClient().PostAsJsonAsync(
            "/api/accounts/connect-manual",
            ManualRequestBuilder.Build("imap.attacker.invalid", "smtp.attacker.invalid", email));

        Assert.Equal(HttpStatusCode.Conflict, attacker.StatusCode);
        var body = await attacker.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("mail_account_already_exists", body!.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.TryGetProperty("accessToken", out _));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.MailAccounts.Include(item => item.Credentials).SingleAsync(item => item.Id == accountId);
        Assert.Equal("mail.test.invalid", account.ImapHost);
        Assert.Equal("mail.test.invalid", account.SmtpHost);
        Assert.Equal(1, await db.MailAccounts.CountAsync(item => item.NormalizedEmailAddress == email.ToUpperInvariant()));
        Assert.Single(account.Credentials);
    }

    [Fact]
    public async Task ConnectManual_ExistingEmail_IssuesNoSessionForTheExistingAccount()
    {
        var email = $"victim-{Guid.NewGuid():N}@example.test";
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", email))).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionsBefore = await db.MailSessions.CountAsync();

        var attacker = await factory.CreateClient().PostAsJsonAsync(
            "/api/accounts/connect-manual",
            ManualRequestBuilder.Build("imap.attacker.invalid", "smtp.attacker.invalid", email));

        Assert.Equal(HttpStatusCode.Conflict, attacker.StatusCode);
        Assert.Equal(sessionsBefore, await db.MailSessions.CountAsync());
    }

    [Fact]
    public async Task Reconnect_UpdatesServerSettingsForTheAuthenticatedAccountOnly()
    {
        var email = $"owner-{Guid.NewGuid():N}@example.test";
        var client = factory.CreateClient();
        var connected = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", email));
        connected.EnsureSuccessStatusCode();
        var tokens = (await connected.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        var accountId = tokens.GetProperty("mailAccountId").GetGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var response = await client.PostAsJsonAsync("/api/account/reconnect", new AccountReconnectRequest(
            new AuthenticationInput(Domain.Enums.AuthenticationMethod.Password, "NewPassword123!"),
            new EndpointInput("imap.newhost.invalid", 993, Domain.Enums.MailSecurity.SslOnConnect),
            new EndpointInput("smtp.newhost.invalid", 465, Domain.Enums.MailSecurity.SslOnConnect)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.MailAccounts.SingleAsync(item => item.Id == accountId);
        Assert.Equal("imap.newhost.invalid", account.ImapHost);
        Assert.Equal("smtp.newhost.invalid", account.SmtpHost);
    }

    [Fact]
    public async Task Reconnect_RequiresAuthentication()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/account/reconnect", new AccountReconnectRequest(
            new AuthenticationInput(Domain.Enums.AuthenticationMethod.Password, "NewPassword123!")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void IssuedToken_ResolvesThroughTheSingleClaimConvention()
    {
        var jwt = factory.Services.GetRequiredService<MailClient.Application.Accounts.IJwtTokenIssuer>();
        var options = factory.Services.GetRequiredService<JwtOptions>();
        var accountId = Guid.NewGuid();

        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
            jwt.Issue(accountId).Token,
            new TokenValidationParameters
            {
                ValidIssuer = options.Issuer,
                ValidAudience = options.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
                NameClaimType = MailAccountClaims.AccountIdClaimType
            },
            out _);

        Assert.True(MailAccountClaims.TryGetAccountId(principal, out var resolved));
        Assert.Equal(accountId, resolved);
    }
}
