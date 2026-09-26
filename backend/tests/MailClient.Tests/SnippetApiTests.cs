using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Api.Endpoints;
using MailClient.Application.Accounts;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class SnippetApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Create_AppendsInOrder_Update_KeepsPosition_Delete_Removes()
    {
        var client = await ClientForNewAccountAsync();

        var first = await CreateAsync(client, new SnippetRequest("  ", "  Teşekkürler.  ", null));
        var second = await CreateAsync(client, new SnippetRequest("Kapanış", "İyi çalışmalar.", null));
        Assert.Null(first.Title);
        Assert.Equal("Teşekkürler.", first.Text);
        Assert.True(second.SortOrder > first.SortOrder);

        var updated = await client.PutAsJsonAsync($"/api/snippets/{first.Id}", new SnippetRequest("Teşekkür", "Bilgi için teşekkür ederim.", null));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedSnippet = (await updated.Content.ReadFromJsonAsync<SnippetResponse>())!;
        Assert.Equal(first.SortOrder, updatedSnippet.SortOrder);
        Assert.Equal("Bilgi için teşekkür ederim.", updatedSnippet.Text);

        var moved = await client.PutAsJsonAsync($"/api/snippets/{first.Id}", new SnippetRequest("Teşekkür", "Bilgi için teşekkür ederim.", second.SortOrder + 1));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var list = await client.GetFromJsonAsync<SnippetListResponse>("/api/snippets");
        Assert.Equal([second.Id, first.Id], list!.Items.Select(x => x.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/snippets/{second.Id}")).StatusCode);
        Assert.Equal(first.Id, Assert.Single((await client.GetFromJsonAsync<SnippetListResponse>("/api/snippets"))!.Items).Id);
    }

    [Theory]
    [InlineData(null, 0, null, "text")]
    [InlineData(null, 2001, null, "text")]
    [InlineData(101, 10, null, "title")]
    [InlineData(null, 10, -1, "sortOrder")]
    public async Task Create_InvalidFields_ReturnsValidationProblemForField(int? titleLength, int textLength, int? sortOrder, string field)
    {
        var client = await ClientForNewAccountAsync();
        var request = new SnippetRequest(titleLength is { } length ? new string('t', length) : null, textLength == 0 ? "   " : new string('x', textLength), sortOrder);

        var response = await client.PostAsJsonAsync("/api/snippets", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Create_AtLengthLimits_Succeeds()
    {
        var client = await ClientForNewAccountAsync();

        var response = await client.PostAsJsonAsync("/api/snippets", new SnippetRequest(new string('t', 100), new string('x', 2000), null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task OtherAccountsSnippet_IsInvisibleAndCannotBeChanged()
    {
        var clientA = await ClientForNewAccountAsync();
        var clientB = await ClientForNewAccountAsync();
        var snippet = await CreateAsync(clientA, new SnippetRequest(null, "Sadece A", null));

        Assert.Empty((await clientB.GetFromJsonAsync<SnippetListResponse>("/api/snippets"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"/api/snippets/{snippet.Id}", new SnippetRequest(null, "B", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"/api/snippets/{snippet.Id}")).StatusCode);

        var own = Assert.Single((await clientA.GetFromJsonAsync<SnippetListResponse>("/api/snippets"))!.Items);
        Assert.Equal("Sadece A", own.Text);
    }

    private static async Task<SnippetResponse> CreateAsync(HttpClient client, SnippetRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/snippets", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SnippetResponse>())!;
    }

    private HttpClient ClientFor(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private async Task<HttpClient> ClientForNewAccountAsync() => ClientFor(await SeedAccountAsync());

    private async Task<Guid> SeedAccountAsync()
    {
        var accountId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@example.test",
            NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
            Username = "snippet-test",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        await db.SaveChangesAsync();
        return accountId;
    }
}
