using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Api.Endpoints;
using MailClient.Application.Runtime;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class WhitelistManagementApiTests(ManagementApiFactory factory) : IClassFixture<ManagementApiFactory>
{
    private const string RuntimeSettingsPath = "/api/management/runtime-settings/";
    private const string WhitelistPath = "/api/management/whitelist";
    private const string ManagementKey = "test-management-key-with-sufficient-length";

    [Fact]
    public async Task AddThenList_ReturnsTheEmailOnce_EvenIfAddedTwice()
    {
        var client = ManagementClient();
        var email = $"user-{Guid.NewGuid():N}@example.test";

        var first = await client.PostAsJsonAsync($"{WhitelistPath}/emails", new AddAllowlistEmailsRequest([email]));
        first.EnsureSuccessStatusCode();
        Assert.Equal([email], (await first.Content.ReadFromJsonAsync<AddAllowlistEmailsResponse>())!.Added);

        var second = await client.PostAsJsonAsync($"{WhitelistPath}/emails", new AddAllowlistEmailsRequest([email.ToUpperInvariant()]));
        second.EnsureSuccessStatusCode();
        Assert.Empty((await second.Content.ReadFromJsonAsync<AddAllowlistEmailsResponse>())!.Added);

        var listed = await client.GetFromJsonAsync<List<AllowlistEmailResponse>>(WhitelistPath);
        Assert.Contains(listed!, item => item.Email == email);
    }

    [Fact]
    public async Task RemovingEmail_WhileEnabled_ImmediatelyDisablesTheMatchingAccount()
    {
        var client = ManagementClient();
        var email = $"user-{Guid.NewGuid():N}@example.test";
        await client.PostAsJsonAsync($"{WhitelistPath}/emails", new AddAllowlistEmailsRequest([email]));
        var accountId = await ConnectAccountAsync(email);
        await SetWhitelistEnabledAsync(client, enabled: true);

        var delete = await client.DeleteAsync($"{WhitelistPath}/emails/{email}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var account = await GetAccountAsync(accountId);
        Assert.Equal(MailAccountStatus.Disabled, account.Status);
        Assert.NotNull(account.AccessRevokedAt);
    }

    [Fact]
    public async Task RemovingEmail_WhileDisabled_DoesNotTouchTheAccount()
    {
        var client = ManagementClient();
        var email = $"user-{Guid.NewGuid():N}@example.test";
        await client.PostAsJsonAsync($"{WhitelistPath}/emails", new AddAllowlistEmailsRequest([email]));
        var accountId = await ConnectAccountAsync(email);
        await SetWhitelistEnabledAsync(client, enabled: false);

        var delete = await client.DeleteAsync($"{WhitelistPath}/emails/{email}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var account = await GetAccountAsync(accountId);
        Assert.Equal(MailAccountStatus.Active, account.Status);
        Assert.Null(account.AccessRevokedAt);
    }

    [Fact]
    public async Task ReAddingEmail_RestoresAnAccountThatWasDisabledForTheAllowlist()
    {
        var client = ManagementClient();
        var email = $"user-{Guid.NewGuid():N}@example.test";
        await client.PostAsJsonAsync($"{WhitelistPath}/emails", new AddAllowlistEmailsRequest([email]));
        var accountId = await ConnectAccountAsync(email);
        await SetWhitelistEnabledAsync(client, enabled: true);
        (await client.DeleteAsync($"{WhitelistPath}/emails/{email}")).EnsureSuccessStatusCode();
        Assert.Equal(MailAccountStatus.Disabled, (await GetAccountAsync(accountId)).Status);

        var readd = await client.PostAsJsonAsync($"{WhitelistPath}/emails", new AddAllowlistEmailsRequest([email]));
        readd.EnsureSuccessStatusCode();

        var account = await GetAccountAsync(accountId);
        Assert.Equal(MailAccountStatus.Active, account.Status);
        Assert.Null(account.AccessRevokedAt);
    }

    [Fact]
    public async Task Emails_RequireManagementKey()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(WhitelistPath)).StatusCode);
    }

    private HttpClient ManagementClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Management-Key", ManagementKey);
        return client;
    }

    private async Task<Guid> ConnectAccountAsync(string email)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/accounts/connect-manual",
            ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", email));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return body!.RootElement.GetProperty("mailAccountId").GetGuid();
    }

    private async Task<Domain.Entities.MailAccount> GetAccountAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MailAccounts.AsNoTracking().SingleAsync(item => item.Id == accountId);
    }

    private static async Task SetWhitelistEnabledAsync(HttpClient client, bool enabled)
    {
        var current = await client.GetFromJsonAsync<RuntimeSettingsResponse>(RuntimeSettingsPath);
        var settings = new RuntimeSettings { Whitelist = new RuntimeWhitelistSettings { Enabled = enabled } };
        (await client.PutAsJsonAsync(RuntimeSettingsPath, new RuntimeSettingsUpdateRequest(current!.Version, settings))).EnsureSuccessStatusCode();
    }
}
