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

public sealed class TemplateApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Create_List_Update_Delete_RoundTrips()
    {
        var client = await ClientForNewAccountAsync();

        var created = await client.PostAsJsonAsync("/api/templates", new TemplateRequest("  Toplantı  ", " Haftalık toplantı ", "Merhaba,\nNotlar ekte.", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var template = (await created.Content.ReadFromJsonAsync<TemplateResponse>())!;
        Assert.Equal("Toplantı", template.Name);
        Assert.Equal("Haftalık toplantı", template.Subject);
        Assert.Null(template.BodyHtml);

        var updated = await client.PutAsJsonAsync($"/api/templates/{template.Id}", new TemplateRequest("TOPLANTI", "", "  ", "<p>Yeni</p>"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedTemplate = (await updated.Content.ReadFromJsonAsync<TemplateResponse>())!;
        Assert.Equal("TOPLANTI", updatedTemplate.Name);
        Assert.Equal("", updatedTemplate.Subject);
        Assert.Null(updatedTemplate.BodyText);
        Assert.Equal("<p>Yeni</p>", updatedTemplate.BodyHtml);
        Assert.Equal(template.CreatedAt, updatedTemplate.CreatedAt);

        var list = await client.GetFromJsonAsync<TemplateListResponse>("/api/templates");
        Assert.Equal(template.Id, Assert.Single(list!.Items).Id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/templates/{template.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<TemplateListResponse>("/api/templates"))!.Items);
    }

    [Fact]
    public async Task DuplicateNameCaseInsensitive_OnCreateAndRename_Returns409WithCode()
    {
        var client = await ClientForNewAccountAsync();
        await client.PostAsJsonAsync("/api/templates", new TemplateRequest("Teşekkür", null, "Teşekkürler.", null));
        var other = (await (await client.PostAsJsonAsync("/api/templates", new TemplateRequest("Ret", null, "Maalesef.", null))).Content.ReadFromJsonAsync<TemplateResponse>())!;

        var duplicate = await client.PostAsJsonAsync("/api/templates", new TemplateRequest("TEŞEKKÜR", null, "x", null));
        var rename = await client.PutAsJsonAsync($"/api/templates/{other.Id}", new TemplateRequest("teşekkür", null, "x", null));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("template_name_taken", await CodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("template_name_taken", await CodeAsync(rename));
    }

    [Theory]
    [InlineData("", "Konu", "Gövde", "name")]
    [InlineData("101", "Konu", "Gövde", "name")]
    [InlineData("Ad", "Konu\r\nBcc: x@example.test", "Gövde", "subject")]
    [InlineData("Ad", "501", "Gövde", "subject")]
    [InlineData("Ad", "Konu", "   ", "body")]
    public async Task Create_InvalidFields_ReturnsValidationProblemForField(string name, string subject, string bodyText, string field)
    {
        var client = await ClientForNewAccountAsync();
        var request = new TemplateRequest(Expand(name), Expand(subject), bodyText, null);

        var response = await client.PostAsJsonAsync("/api/templates", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Create_AtLengthLimits_Succeeds()
    {
        var client = await ClientForNewAccountAsync();

        var response = await client.PostAsJsonAsync("/api/templates", new TemplateRequest(new string('a', 100), new string('b', 500), null, "<p>x</p>"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task OtherAccountsTemplate_IsInvisibleAndCannotBeChanged()
    {
        var clientA = await ClientForNewAccountAsync();
        var clientB = await ClientForNewAccountAsync();
        var template = (await (await clientA.PostAsJsonAsync("/api/templates", new TemplateRequest("Özel", "Konu", "Gövde", null))).Content.ReadFromJsonAsync<TemplateResponse>())!;

        Assert.Empty((await clientB.GetFromJsonAsync<TemplateListResponse>("/api/templates"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"/api/templates/{template.Id}", new TemplateRequest("Çalındı", null, "x", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"/api/templates/{template.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await clientB.PostAsJsonAsync("/api/templates", new TemplateRequest("Özel", null, "B'nin", null))).StatusCode);

        var ownList = await clientA.GetFromJsonAsync<TemplateListResponse>("/api/templates");
        var own = Assert.Single(ownList!.Items);
        Assert.Equal("Özel", own.Name);
        Assert.Equal("Gövde", own.BodyText);
    }

    private static string Expand(string value) => int.TryParse(value, out var length) ? new string('x', length) : value;

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
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
            Username = "template-test",
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
