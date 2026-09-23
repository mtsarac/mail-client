using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MailClient.Tests;

public sealed class MalformedRequestApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Theory]
    [InlineData("/api/mails/send")]
    [InlineData("/api/drafts")]
    public async Task TruncatedMultipartBody_IsClientError(string path)
    {
        var client = await ConnectAsync();
        var content = new StringContent("--b\r\nContent-Disposition: form-data; name=\"subject\"\r\n\r\nhel", Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=b");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        // A truncated body surfaces as an IOException that minimal APIs answer with a bare 400 (not a 500).
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJsonBody_IsClientError()
    {
        var response = await factory.CreateClient().PostAsync("/api/accounts/login",
            new StringContent("{\"email\":", Encoding.UTF8, "application/json"));

        await AssertInvalidRequestAsync(response);
    }

    private static async Task AssertInvalidRequestAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("invalid_request", problem!.RootElement.GetProperty("code").GetString());
    }

    private async Task<HttpClient> ConnectAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/connect-manual",
            ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", $"{Guid.NewGuid():N}@mail.test.invalid"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<JsonDocument>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
