using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

internal sealed class StubMailValidator(bool accept) : IMailConnectionValidator, IMailServerCandidateValidator
{
    public Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) =>
        Task.FromResult(accept);
    public Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) =>
        Task.FromResult(accept);
    public Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken) =>
        accept ? Task.CompletedTask : throw new InvalidOperationException("mail_server_unsafe");
}

public class MailClientApiFactory : WebApplicationFactory<Program>
{
    protected virtual bool AcceptCandidates => true;
    public string DbName { get; } = Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList())
                services.Remove(descriptor);
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IMailConnectionValidator) || d.ServiceType == typeof(IMailServerCandidateValidator)).ToList())
                services.Remove(descriptor);
            var internalProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
            services.AddDbContext<AppDbContext>(options => options
                .UseInMemoryDatabase(DbName)
                .UseInternalServiceProvider(internalProvider));
            services.AddSingleton<IMailConnectionValidator>(new StubMailValidator(AcceptCandidates));
            services.AddSingleton<IMailServerCandidateValidator>(sp => (StubMailValidator)sp.GetRequiredService<IMailConnectionValidator>());
        });
    }
}

public sealed class AcceptingApiFactory : MailClientApiFactory
{
    protected override bool AcceptCandidates => true;
}

public sealed class RejectingApiFactory : MailClientApiFactory
{
    protected override bool AcceptCandidates => false;
}

internal static class ManualRequestBuilder
{
    public static object Build(string imapHost, string smtpHost, string email = "person@mail.test.invalid") => new
    {
        email,
        username = email,
        authentication = new { type = "Password", password = "ExamplePassword123!" },
        imap = new { host = imapHost, port = 993, security = "SslOnConnect" },
        smtp = new { host = smtpHost, port = 465, security = "SslOnConnect" }
    };
}

public sealed class DiscoveryFailureApiTests(RejectingApiFactory factory) : IClassFixture<RejectingApiFactory>
{
    private readonly RejectingApiFactory _rejecting = factory;

    [Fact]
    public async Task Discover_AllStrategiesFail_422WithoutPersistence()
    {
        var factory = _rejecting;
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/discover", new { email = "person@mail.test.invalid" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("mail_discovery_failed", body!.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("manualSetupAvailable").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.MailAccounts.CountAsync());
        Assert.Equal(0, await db.MailCredentials.CountAsync());
    }

    [Fact]
    public async Task ConnectManual_UnsafeHost_RejectedWithoutPersistence()
    {
        var factory = _rejecting;
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("imap.localhost", "smtp.localhost"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("mail_server_unsafe", body!.RootElement.GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.MailAccounts.CountAsync());
        Assert.Equal(0, await db.MailCredentials.CountAsync());
    }

}

public sealed class AccountApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    private readonly AcceptingApiFactory _accepting = factory;

    [Fact]
    public async Task ConnectManual_Success_IssuesAccountBoundTokens()
    {
        var factory = _accepting;
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var accessToken = tokens!.RootElement.GetProperty("accessToken").GetString()!;
        var refreshToken = tokens.RootElement.GetProperty("refreshToken").GetString()!;
        var accountId = tokens.RootElement.GetProperty("mailAccountId").GetGuid();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshToken));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.MailAccounts.Include(x => x.Credentials).SingleAsync(x => x.Id == accountId);
        Assert.Equal("person@mail.test.invalid", account.EmailAddress);
        var credential = Assert.Single(account.Credentials);
        Assert.DoesNotContain("ExamplePassword123!", credential.EncryptedMaterial);
    }

    [Fact]
    public async Task MailDetail_IsIsolatedBetweenAccounts()
    {
        var factory = _accepting;
        var issuer = factory.Services.GetRequiredService<IJwtTokenIssuer>();
        var accountA = Guid.NewGuid();
        var mailA = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MailAccounts.Add(new MailAccount
            {
                Id = accountA,
                EmailAddress = "a@mail.test.invalid",
                NormalizedEmailAddress = "A@MAIL.TEST.INVALID",
                Username = "a",
                ImapHost = "mail.test.invalid",
                ImapPort = 993,
                SmtpHost = "mail.test.invalid",
                SmtpPort = 465,
                Status = MailAccountStatus.Active
            });
            db.Mails.Add(new Domain.Entities.Mail
            {
                Id = mailA,
                MailAccountId = accountA,
                MailFolderId = Guid.NewGuid(),
                Uid = 1,
                Subject = "secret",
                FromAddress = "x@y.test"
            });
            await db.SaveChangesAsync();
        }

        var tokenA = issuer.Issue(accountA).Token;
        var clientB = factory.CreateClient();
        var connectB = await clientB.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", "b@mail.test.invalid"));
        connectB.EnsureSuccessStatusCode();
        var tokenB = (await connectB.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("accessToken").GetString()!;

        var asB = factory.CreateClient();
        asB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var isoResponse = await asB.GetAsync($"/api/mails/{mailA}");
        Assert.Equal(HttpStatusCode.NotFound, isoResponse.StatusCode);

        var asA = factory.CreateClient();
        asA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        Assert.Equal(HttpStatusCode.OK, (await asA.GetAsync($"/api/mails/{mailA}")).StatusCode);
    }

    [Fact]
    public async Task Refresh_Rotates_LogoutRevokes()
    {
        var factory = _accepting;
        var client = factory.CreateClient();
        var connect = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", "c@mail.test.invalid"));
        connect.EnsureSuccessStatusCode();
        var firstRefresh = (await connect.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("refreshToken").GetString()!;

        var rotated = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = firstRefresh });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var secondRefresh = (await rotated.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(firstRefresh, secondRefresh);

        var reused = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = firstRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = secondRefresh });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = secondRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

}
