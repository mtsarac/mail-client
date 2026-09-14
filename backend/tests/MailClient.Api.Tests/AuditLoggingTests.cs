using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Domain;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Api.Tests;

public sealed class AuditLoggingTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Register_PersistsAuditWithoutSecrets()
    {
        var email = UniqueEmail("audit-reg");
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "test-corr-1");
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "audit-password-1",
            displayName = "Audit"
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        var log = await db.AuditLogs.SingleOrDefaultAsync(
            l => l.Action == AuditActions.UserRegistered && l.UserId == user.Id);
        Assert.NotNull(log);
        Assert.Equal("test-corr-1", log.CorrelationId);
        Assert.DoesNotContain("audit-password-1", log.Metadata ?? "");
    }

    [Fact]
    public async Task FailedLogin_WritesNoAudit()
    {
        var email = UniqueEmail("audit-fail");
        var (userId, _) = await SeedUserAsync(email, "right-password-1");
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.AuditLogs
            .Where(l => l.Action == AuditActions.UserLoggedIn && l.UserId == userId)
            .ToListAsync());
    }

    [Fact]
    public async Task SuccessfulLogin_PersistsAuditWithEchoedCorrelation()
    {
        var email = UniqueEmail("audit-login");
        var (userId, _) = await SeedUserAsync(email, "login-password-1");
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password = "login-password-1" })
        };
        request.Headers.Add("X-Correlation-ID", "login-corr-9");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("login-corr-9", response.Headers.GetValues("X-Correlation-ID").Single());

        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = await db.AuditLogs.SingleOrDefaultAsync(
            l => l.Action == AuditActions.UserLoggedIn && l.UserId == userId);
        Assert.NotNull(log);
        Assert.Equal("login-corr-9", log.CorrelationId);
    }

    [Fact]
    public async Task MailAccountCreate_PersistsAuditWithoutMailboxPassword()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("audit-acct"), "acct-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client, AccountPayload("mailbox-secret-xyz"));

        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = await db.AuditLogs.SingleOrDefaultAsync(
            l => l.Action == AuditActions.MailAccountCreated && l.EntityId == accountId.ToString());
        Assert.NotNull(log);
        Assert.DoesNotContain("mailbox-secret-xyz", log.Metadata ?? "");
    }

    [Fact]
    public async Task MarkReadNoOp_WritesNoDuplicateAudit()
    {
        var (userId, token) = await SeedUserAsync(UniqueEmail("audit-read"), "read-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);
        var mailId = await SeedMailAsync(userId, accountId, await SeedFolderAsync(accountId, Domain.Enums.MailFolderType.Inbox), "noop");

        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mail = await db.Mails.SingleAsync(m => m.Id == mailId);
            mail.IsRead = true;
            await db.SaveChangesAsync();
        }

        var before = await AuditCountAsync(mailId);
        var response = await client.PatchAsJsonAsync($"/api/mails/{mailId}/read", new { isRead = true });
        response.EnsureSuccessStatusCode();
        Assert.Equal(before, await AuditCountAsync(mailId));
    }

    private async Task<int> AuditCountAsync(Guid mailId)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.CountAsync(
            l => l.Action == AuditActions.MailReadStateChanged && l.EntityId == mailId.ToString());
    }

    private async Task<Guid> SeedFolderAsync(Guid accountId, Domain.Enums.MailFolderType type)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var folder = new Domain.Entities.MailFolder
        {
            MailAccountId = accountId,
            Name = type.ToString(),
            FullName = type.ToString(),
            FolderType = type,
            UidValidity = 1,
            IsSyncEnabled = true,
            IsAvailable = true
        };
        db.MailFolders.Add(folder);
        await db.SaveChangesAsync();
        return folder.Id;
    }

    private async Task<Guid> SeedMailAsync(Guid userId, Guid accountId, Guid folderId, string subject)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mail = new Domain.Entities.Mail
        {
            MailAccountId = accountId,
            MailFolderId = folderId,
            Subject = subject,
            FromAddress = "a@example.com",
            FromDisplayName = "A",
            ToAddress = "b@example.com",
            ReceivedAt = DateTime.UtcNow,
            IsRead = false,
            HasAttachments = false,
            Uid = 1,
            UidValidity = 1
        };
        db.Mails.Add(mail);
        await db.SaveChangesAsync();
        return mail.Id;
    }
}
