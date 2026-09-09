using System.Net;

namespace MailClient.Api.Tests;

public sealed class MailRateLimitTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task RepeatedConnectionTests_AreThrottledWith429()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail(), "owner-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            var response = await client.PostAsync($"/api/mail-accounts/{accountId}/test", null);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Contains(statuses, status => status == HttpStatusCode.TooManyRequests);
    }
}
