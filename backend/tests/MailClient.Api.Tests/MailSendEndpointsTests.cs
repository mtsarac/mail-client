using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

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

        var response = await client.PostAsync(
            $"/api/mail-accounts/{accountId}/send", SendForm("not-an-address", "Hi", "hello"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Send_MissingBody_ReturnsBadRequest()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("senderval"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);

        var form = new MultipartFormDataContent
        {
            { new StringContent("friend@example.test"), "toAddress" },
            { new StringContent("Hi"), "subject" }
        };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync($"/api/mail-accounts/{accountId}/send", form)).StatusCode);
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

        var response = await clientB.PostAsync(
            $"/api/mail-accounts/{accountId}/send", SendForm("friend@example.test", "Hi", "hello"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Send_UnreachableSmtp_ReturnsBadGateway()
    {
        var (_, token) = await SeedUserAsync(UniqueEmail("senderdown"), "sender-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateUnreachableAccountAsync(client);

        var response = await client.PostAsync(
            $"/api/mail-accounts/{accountId}/send", SendForm("friend@example.test", "Hi", "hello"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
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
