using System.Net;
using System.Net.Http.Json;
using MailClient.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Api.Tests;

public sealed class TokenInvalidationTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task DisableUser_InvalidatesExistingToken()
    {
        var (userId, staleToken) = await SeedUserAsync(UniqueEmail(), "correct-password-1");
        var (_, adminToken) = await SeedUserAsync(UniqueEmail("admin"), "admin-password-1", UserRole.Admin);

        var userClient = CreateClient();
        Authenticate(userClient, staleToken);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/mail-accounts/")).StatusCode);

        var adminClient = CreateClient();
        Authenticate(adminClient, adminToken);
        var disable = await adminClient.PatchAsync($"/api/admin/users/{userId}/disable", null);
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/mail-accounts/")).StatusCode);
    }

    [Fact]
    public async Task PasswordReset_InvalidatesExistingToken_AndNewLoginWorks()
    {
        var email = UniqueEmail();
        var (userId, staleToken) = await SeedUserAsync(email, "old-password-1");
        var (_, adminToken) = await SeedUserAsync(UniqueEmail("admin"), "admin-password-1", UserRole.Admin);

        var adminClient = CreateClient();
        Authenticate(adminClient, adminToken);
        var reset = await adminClient.PostAsJsonAsync(
            $"/api/admin/users/{userId}/reset-password", new { password = "brand-new-password-1" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var staleClient = CreateClient();
        Authenticate(staleClient, staleToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staleClient.GetAsync("/api/mail-accounts/")).StatusCode);

        var loginClient = CreateClient();
        var login = await loginClient.PostAsJsonAsync("/api/auth/login", new { email, password = "brand-new-password-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task RoleChange_TakesEffectWithoutNewLogin()
    {
        var (userId, token) = await SeedUserAsync(UniqueEmail(), "correct-password-1");
        var client = CreateClient();
        Authenticate(client, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users/")).StatusCode);

        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MailClient.Infrastructure.Persistence.AppDbContext>();
        var user = await db.Users.FindAsync(userId);
        Assert.NotNull(user);
        user.Role = UserRole.Admin;
        await db.SaveChangesAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/users/")).StatusCode);
    }
}
