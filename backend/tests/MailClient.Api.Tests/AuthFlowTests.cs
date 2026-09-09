using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MailClient.Api.Tests;

public sealed class AuthFlowTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Register_Login_ProtectedEndpoint_Succeeds()
    {
        var client = CreateClient();
        var email = UniqueEmail();

        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct-password-1",
            displayName = "Flow User"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "correct-password-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        Authenticate(client, token!);
        var protectedCall = await client.GetAsync("/api/mail-accounts/");
        Assert.Equal(HttpStatusCode.OK, protectedCall.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/mail-accounts/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithInvalidToken_Returns401()
    {
        var client = CreateClient();
        Authenticate(client, "this-is-not-a-valid-token");

        var response = await client.GetAsync("/api/mail-accounts/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var client = CreateClient();
        var email = UniqueEmail();
        var payload = new { email, password = "correct-password-1", displayName = "Dup User" };

        var first = await client.PostAsJsonAsync("/api/auth/register", payload);
        var second = await client.PostAsJsonAsync("/api/auth/register", payload);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
}
