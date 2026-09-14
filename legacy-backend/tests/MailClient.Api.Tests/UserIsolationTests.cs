using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MailClient.Api.Tests;

public sealed class UserIsolationTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task SecondUser_CannotAccessFirstUsersAccount()
    {
        var (_, tokenA) = await SeedUserAsync(UniqueEmail("alice"), "alice-password-1");
        var (_, tokenB) = await SeedUserAsync(UniqueEmail("bob"), "bob-password-1");

        var clientA = CreateClient();
        Authenticate(clientA, tokenA);
        var accountId = await CreateAccountAsync(clientA);

        var clientB = CreateClient();
        Authenticate(clientB, tokenB);

        var list = await clientB.GetFromJsonAsync<JsonElement>("/api/mail-accounts/");
        Assert.Equal(0, list.GetArrayLength());

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/mail-accounts/{accountId}")).StatusCode);

        var update = await clientB.PutAsJsonAsync($"/api/mail-accounts/{accountId}", AccountPayload());
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"/api/mail-accounts/{accountId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsync($"/api/mail-accounts/{accountId}/test", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsync($"/api/mail-accounts/{accountId}/folders/refresh", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/mail-accounts/{accountId}/folders")).StatusCode);
    }

    [Fact]
    public async Task Owner_CanUseFullAccountLifecycle()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail(), "owner-password-1");
        var client = CreateClient();
        Authenticate(client, token);

        var accountId = await CreateAccountAsync(client);

        var test = await client.PostAsync($"/api/mail-accounts/{accountId}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);

        var folders = await client.GetFromJsonAsync<JsonElement>($"/api/mail-accounts/{accountId}/folders");
        Assert.Equal(2, folders.GetArrayLength());

        var delete = await client.DeleteAsync($"/api/mail-accounts/{accountId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/mail-accounts/{accountId}")).StatusCode);
    }
}
