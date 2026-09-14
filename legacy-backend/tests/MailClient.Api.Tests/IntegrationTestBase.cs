using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Api.Tests;

[Collection("integration")]
public abstract class IntegrationTestBase(IntegrationFixture fixture)
{
    protected IntegrationFixture Fixture { get; } = fixture;

    protected HttpClient CreateClient() => Fixture.Factory.CreateClient();

    protected static string UniqueEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    protected async Task<(Guid Id, string Token)> SeedUserAsync(
        string email, string password, UserRole role = UserRole.User, UserStatus status = UserStatus.Active)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<AppDbContext>();
        var hasher = provider.GetRequiredService<IPasswordHasher<User>>();
        var issuer = provider.GetRequiredService<IJwtTokenIssuer>();

        var user = new User
        {
            Email = email.ToLowerInvariant(),
            DisplayName = "Test User",
            Role = role,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (user.Id, issuer.IssueToken(user.Id, user.Role, user.TokenVersion));
    }

    protected static void Authenticate(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    protected static object AccountPayload(string password = "mailbox-secret-1") => new
    {
        emailAddress = UniqueEmail("mailbox"),
        displayName = "Mailbox",
        username = "mailbox-user",
        password,
        imapHost = "imap.example.com",
        imapPort = 993,
        imapSecurity = "SslOnConnect",
        smtpHost = "smtp.example.com",
        smtpPort = 587,
        smtpSecurity = "StartTls",
        saveSentCopy = true
    };

    protected static async Task<Guid> CreateAccountAsync(HttpClient client, object? payload = null)
    {
        var response = await client.PostAsJsonAsync("/api/mail-accounts", payload ?? AccountPayload());
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }
}
