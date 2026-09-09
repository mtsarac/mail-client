using System.Net;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Api.Tests;

public sealed class PersistenceTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Migrations_ApplyCleanly_OnFreshDatabase()
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.Contains(applied, name => name.Contains("InitialMultiAccountSchema"));
        Assert.Contains(applied, name => name.Contains("AddUserTokenVersion"));
        Assert.True(await db.Database.CanConnectAsync());
    }

    [Fact]
    public async Task DeleteAccount_CascadesToFolders()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail(), "owner-password-1");
        var client = CreateClient();
        Authenticate(client, token);

        var accountId = await CreateAccountAsync(client);
        var refresh = await client.PostAsync($"/api/mail-accounts/{accountId}/folders/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(2, await db.MailFolders.CountAsync(folder => folder.MailAccountId == accountId));
        }

        var delete = await client.DeleteAsync($"/api/mail-accounts/{accountId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using (var scope = Fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.MailFolders.CountAsync(folder => folder.MailAccountId == accountId));
        }
    }
}
