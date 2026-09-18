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
    public Task ValidateOAuthCredentialsAsync(MailServerCandidate candidate, string username, string accessToken, CancellationToken cancellationToken) =>
        accept ? Task.CompletedTask : throw new InvalidOperationException("mail_server_unsafe");
}

public class MailClientApiFactory : WebApplicationFactory<Program>
{
    protected virtual bool AcceptCandidates => true;
    public string DbName { get; } = Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
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
    public async Task CorrelationId_IsSharedByResponseAuditAndProblemDetails()
    {
        var client = _accepting.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "client-correlation-1");

        var discovery = await client.PostAsJsonAsync("/api/accounts/discover", new { email = "person@gmail.com" });

        Assert.Equal("client-correlation-1", discovery.Headers.GetValues("X-Correlation-ID").Single());
        using (var scope = _accepting.Services.CreateScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.SingleAsync();
            Assert.Equal("client-correlation-1", audit.CorrelationId);
        }

        var problem = await client.PostAsJsonAsync("/api/accounts/connect", new { discoveryId = Guid.NewGuid().ToString("N") });
        var body = await problem.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problem.StatusCode);
        Assert.Equal("discovery_expired", body!.RootElement.GetProperty("code").GetString());
        Assert.Equal("client-correlation-1", body.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task CorrelationId_InvalidHeader_IsReplaced()
    {
        var client = _accepting.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-ID", "forged\r\nvalue");

        var response = await client.GetAsync("/health");

        Assert.NotEqual("forged\r\nvalue", response.Headers.GetValues("X-Correlation-ID").Single());
    }

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

    [Theory]
    [InlineData(true, true, HttpStatusCode.Accepted)]
    [InlineData(false, true, HttpStatusCode.Accepted)]
    [InlineData(true, false, HttpStatusCode.Conflict)]
    public async Task SyncFolder_UsesExplicitAvailabilitySemantics(bool enabled, bool available, HttpStatusCode expected)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        using (var scope = _accepting.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MailAccounts.Add(new MailAccount
            {
                Id = accountId,
                EmailAddress = $"{accountId:N}@mail.test.invalid",
                NormalizedEmailAddress = $"{accountId:N}@MAIL.TEST.INVALID",
                Username = accountId.ToString("N"),
                ImapHost = "mail.test.invalid",
                ImapPort = 993,
                SmtpHost = "mail.test.invalid",
                SmtpPort = 465,
                Status = MailAccountStatus.Active
            });
            db.MailFolders.Add(new Domain.Entities.MailFolder
            {
                Id = folderId,
                MailAccountId = accountId,
                Name = "INBOX",
                FullName = "INBOX",
                IsSyncEnabled = enabled,
                IsAvailable = available
            });
            await db.SaveChangesAsync();
        }

        var client = _accepting.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accepting.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        var response = await client.PostAsync($"/api/folders/{folderId}/sync", null);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("mail_folder_unavailable", body!.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task SyncFolder_ForeignFolder_ReturnsNotFound()
    {
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        using (var scope = _accepting.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var accountId in new[] { ownerId, callerId })
                db.MailAccounts.Add(new MailAccount
                {
                    Id = accountId,
                    EmailAddress = $"{accountId:N}@mail.test.invalid",
                    NormalizedEmailAddress = $"{accountId:N}@MAIL.TEST.INVALID",
                    Username = accountId.ToString("N"),
                    ImapHost = "mail.test.invalid",
                    ImapPort = 993,
                    SmtpHost = "mail.test.invalid",
                    SmtpPort = 465,
                    Status = MailAccountStatus.Active
                });
            db.MailFolders.Add(new Domain.Entities.MailFolder
            {
                Id = folderId,
                MailAccountId = ownerId,
                Name = "INBOX",
                FullName = "INBOX",
                IsAvailable = true
            });
            await db.SaveChangesAsync();
        }

        var client = _accepting.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accepting.Services.GetRequiredService<IJwtTokenIssuer>().Issue(callerId).Token);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/folders/{folderId}/sync", null)).StatusCode);
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
        Assert.True(rotated.StatusCode == HttpStatusCode.OK, await rotated.Content.ReadAsStringAsync());
        var secondRefresh = (await rotated.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(firstRefresh, secondRefresh);

        var reused = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = firstRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = secondRefresh });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = secondRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Login_ExistingAccount_IssuesSeparateSessionForSecondDevice()
    {
        var factory = _accepting;
        var deviceA = factory.CreateClient();
        var connect = await deviceA.PostAsJsonAsync("/api/accounts/connect-manual", new
        {
            email = "multi-device@mail.test.invalid",
            username = "multi-device@mail.test.invalid",
            authentication = new { type = "Password", password = "ExamplePassword123!" },
            imap = new { host = "mail.test.invalid", port = 993, security = "SslOnConnect" },
            smtp = new { host = "mail.test.invalid", port = 465, security = "SslOnConnect" },
            deviceIdentifier = "device-a"
        });
        connect.EnsureSuccessStatusCode();
        var accountId = (await connect.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("mailAccountId").GetGuid();

        var deviceB = factory.CreateClient();
        var login = await deviceB.PostAsJsonAsync("/api/accounts/login", new { email = "multi-device@mail.test.invalid", password = "ExamplePassword123!", deviceIdentifier = "device-b" });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = await login.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(accountId, loginBody!.RootElement.GetProperty("mailAccountId").GetGuid());
        var tokenB = loginBody.RootElement.GetProperty("accessToken").GetString()!;

        var asB = factory.CreateClient();
        asB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var sessions = await asB.GetFromJsonAsync<JsonDocument>("/api/account/sessions");
        var deviceIdentifiers = sessions!.RootElement.EnumerateArray().Select(x => x.GetProperty("deviceIdentifier").GetString()).ToList();
        Assert.Contains("device-a", deviceIdentifiers);
        Assert.Contains("device-b", deviceIdentifiers);
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsNotFound()
    {
        var response = await _accepting.CreateClient().PostAsJsonAsync("/api/accounts/login", new { email = "nobody@mail.test.invalid", password = "ExamplePassword123!" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("mail_account_not_found", body!.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_RejectedCredentials_DoesNotIssueSession()
    {
        var factory = new RejectingApiFactory();
        var accountId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MailAccounts.Add(new MailAccount
            {
                Id = accountId,
                EmailAddress = "wrong-pass@mail.test.invalid",
                NormalizedEmailAddress = "WRONG-PASS@MAIL.TEST.INVALID",
                Username = "wrong-pass@mail.test.invalid",
                ImapHost = "mail.test.invalid",
                ImapPort = 993,
                SmtpHost = "mail.test.invalid",
                SmtpPort = 465,
                Status = MailAccountStatus.Active
            });
            await db.SaveChangesAsync();
        }

        var response = await factory.CreateClient().PostAsJsonAsync("/api/accounts/login", new { email = "wrong-pass@mail.test.invalid", password = "WrongPassword!" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var scope2 = factory.Services.CreateScope();
        Assert.Equal(0, await scope2.ServiceProvider.GetRequiredService<AppDbContext>().MailSessions.CountAsync());
    }

    [Fact]
    public async Task RevokeSession_ForeignSession_ReturnsNotFound()
    {
        var factory = _accepting;
        var connectA = await factory.CreateClient().PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", "revoke-a@mail.test.invalid"));
        connectA.EnsureSuccessStatusCode();
        var bodyA = await connectA.Content.ReadFromJsonAsync<JsonDocument>();
        var accountAId = bodyA!.RootElement.GetProperty("mailAccountId").GetGuid();
        Guid sessionAId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            sessionAId = await db.MailSessions.Where(x => x.MailAccountId == accountAId).Select(x => x.Id).SingleAsync();
        }

        var connectB = await factory.CreateClient().PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", "revoke-b@mail.test.invalid"));
        connectB.EnsureSuccessStatusCode();
        var tokenB = (await connectB.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("accessToken").GetString()!;

        var asB = factory.CreateClient();
        asB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        var response = await asB.DeleteAsync($"/api/account/sessions/{sessionAId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

}
