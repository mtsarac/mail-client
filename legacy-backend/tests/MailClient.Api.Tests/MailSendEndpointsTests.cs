using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailClient.Application.Sync;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MailClient.Api.Tests;

public sealed class MailSendEndpointsTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Anonymous_Send_ReturnsUnauthorized()
    {
        var client = CreateClient();
        var content = SendForm("friend@example.test", "Hi", "hello");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync($"/api/mail-accounts/{Guid.NewGuid()}/send", content)).StatusCode);
    }

    [Fact]
    public async Task Send_InvalidRecipient_ReturnsBadRequest()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("senderval"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);

        var response = await client.SendAsync(KeyedRequest(accountId, $"key-{Guid.NewGuid():N}", "Hi", "not-an-address"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Send_MissingIdempotencyKey_ReturnsBadRequest()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("senderkey"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);

        var response = await client.PostAsync(
            $"/api/mail-accounts/{accountId}/send", SendForm("friend@example.test", "Hi", "hello"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Send_MissingBody_ReturnsBadRequest()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("senderval"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/mail-accounts/{accountId}/send");
        request.Content = new MultipartFormDataContent
        {
            { new StringContent("friend@example.test"), "toAddress" },
            { new StringContent("Hi"), "subject" }
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Send_OtherUsersAccount_ReturnsNotFound()
    {
        var (_, tokenA) = await SeedUserAsync(UniqueEmail("sendera"), "sender-password-1");
        var clientA = CreateClient();
        Authenticate(clientA, tokenA);
        var accountId = await CreateAccountAsync(clientA);

        var (_, tokenB) = await SeedUserAsync(UniqueEmail("senderb"), "sender-password-1");
        var clientB = CreateClient();
        Authenticate(clientB, tokenB);

        var request = KeyedRequest(accountId, $"key-{Guid.NewGuid():N}", "Hi");
        var response = await clientB.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Send_UnreachableSmtp_ReturnsBadGateway()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("senderdown"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateUnreachableAccountAsync(client);

        var response = await client.SendAsync(KeyedRequest(accountId, $"key-{Guid.NewGuid():N}", "Hi"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Send_EmptyIdempotencyKey_ReturnsBadRequest()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("senderkey"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/mail-accounts/{accountId}/send");
        request.Content = SendForm("friend@example.test", "Hi", "hello");
        request.Headers.Add("Idempotency-Key", "   ");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);

        var longKey = new HttpRequestMessage(HttpMethod.Post, $"/api/mail-accounts/{accountId}/send");
        longKey.Content = SendForm("friend@example.test", "Hi", "hello");
        longKey.Headers.Add("Idempotency-Key", new string('k', 201));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(longKey)).StatusCode);
    }

    [Fact]
    public async Task Send_SameKeyTwice_FailedOperation_AllowsRetry()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("senderretry"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateUnreachableAccountAsync(client);

        async Task<HttpStatusCode> Send() =>
            (await client.SendAsync(KeyedRequest(accountId, "retry-key-1", "Hi"))).StatusCode;
        Assert.Equal(HttpStatusCode.BadGateway, await Send());
        Assert.Equal(HttpStatusCode.BadGateway, await Send());
    }

    [Fact]
    public async Task Send_SameKeyDifferentBody_ReturnsConflict()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("sendermix"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateUnreachableAccountAsync(client);

        Assert.Equal(HttpStatusCode.BadGateway, (await client.SendAsync(KeyedRequest(accountId, "mix-key-1", "Hi"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(KeyedRequest(accountId, "mix-key-1", "Changed"))).StatusCode);
    }

    private static HttpRequestMessage KeyedRequest(Guid accountId, string key, string subject, string to = "friend@example.test")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/mail-accounts/{accountId}/send");
        request.Content = SendForm(to, subject, "hello");
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public void MultipartLimit_MatchesComputedSendCeiling()
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var ceiling = SendRequestLimits.ComputeMaxRequestBytes(
            provider.GetRequiredService<MailSyncOptions>());

        Assert.Equal(ceiling, provider.GetRequiredService<IOptions<FormOptions>>().Value.MultipartBodyLengthLimit);
    }

    private static MultipartFormDataContent SendForm(string to, string subject, string body)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(to), "toAddress" },
            { new StringContent(subject), "subject" },
            { new StringContent(body, Encoding.UTF8, "text/plain"), "bodyText" }
        };
        return form;
    }

    private static async Task<Guid> CreateUnreachableAccountAsync(HttpClient client)
    {
        var payload = new
        {
            emailAddress = UniqueEmail("nosmtp"),
            displayName = "No Smtp",
            username = "nosmtp",
            password = "mailbox-secret-1",
            imapHost = "imap.invalid",
            imapPort = 993,
            imapSecurity = "SslOnConnect",
            smtpHost = "smtp.invalid",
            smtpPort = 587,
            smtpSecurity = "StartTls",
            saveSentCopy = true
        };
        var response = await client.PostAsJsonAsync("/api/mail-accounts", payload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }
}
