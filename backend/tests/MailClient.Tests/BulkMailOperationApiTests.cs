using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Api.Endpoints;

namespace MailClient.Tests;

public sealed class BulkMailOperationApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task UnknownAction_Returns404()
    {
        var client = await ConnectAsync();

        var response = await client.PostAsJsonAsync("/api/mails/bulk/unknown-action", new BulkMailOperationRequest([Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EmptyMailIds_ReturnsValidationProblem()
    {
        var client = await ConnectAsync();

        var response = await client.PostAsJsonAsync("/api/mails/bulk/trash", new BulkMailOperationRequest([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TooManyMailIds_ReturnsValidationProblem()
    {
        var client = await ConnectAsync();
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();

        var response = await client.PostAsJsonAsync("/api/mails/bulk/trash", new BulkMailOperationRequest(ids));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Move_WithoutFolderId_ReturnsValidationProblem()
    {
        var client = await ConnectAsync();

        var response = await client.PostAsJsonAsync("/api/mails/bulk/move", new BulkMailOperationRequest([Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownMailIds_ReportPerItemNotFound_WithoutThrowing()
    {
        var client = await ConnectAsync();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var response = await client.PostAsJsonAsync("/api/mails/bulk/trash", new BulkMailOperationRequest(ids));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BulkMailOperationResponse>();
        Assert.Equal(2, body!.Results.Count);
        Assert.All(body.Results, item => Assert.False(item.Success));
        Assert.All(body.Results, item => Assert.Equal("mail_not_found", item.Code));
    }

    private async Task<HttpClient> ConnectAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", $"{Guid.NewGuid():N}@mail.test.invalid"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<JsonDocument>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.RootElement.GetProperty("accessToken").GetString());
        return client;
    }
}
