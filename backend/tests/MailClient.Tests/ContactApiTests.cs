using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MailClient.Api.Endpoints;
using MailClient.Application.Accounts;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class ContactApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Create_List_Update_Delete_RoundTrips()
    {
        var client = await ClientForNewAccountAsync();

        var created = await client.PostAsJsonAsync("/api/contacts", new ContactRequest("ayse@example.test", "Ayşe"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var contact = (await created.Content.ReadFromJsonAsync<ContactResponse>())!;
        Assert.Equal("ayse@example.test", contact.Email);

        var list = await client.GetFromJsonAsync<ContactListResponse>("/api/contacts");
        Assert.Single(list!.Items);

        var updated = await client.PutAsJsonAsync($"/api/contacts/{contact.Id}", new ContactRequest("ayse2@example.test", "Ayşe K."));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Ayşe K.", (await updated.Content.ReadFromJsonAsync<ContactResponse>())!.DisplayName);

        var deleted = await client.DeleteAsync($"/api/contacts/{contact.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ContactListResponse>("/api/contacts"))!.Items);
    }

    [Fact]
    public async Task Create_DuplicateEmailCaseInsensitive_Returns409()
    {
        var client = await ClientForNewAccountAsync();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/contacts", new ContactRequest("dup@example.test", null))).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/contacts", new ContactRequest("DUP@Example.Test", null));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidEmail_ReturnsValidationProblem()
    {
        var client = await ClientForNewAccountAsync();

        var response = await client.PostAsJsonAsync("/api/contacts", new ContactRequest("not-an-email", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Contacts_AreScopedPerAccount()
    {
        var accountA = await SeedAccountAsync();
        var accountB = await SeedAccountAsync();
        var clientA = ClientFor(accountA);
        var clientB = ClientFor(accountB);
        await clientA.PostAsJsonAsync("/api/contacts", new ContactRequest("only-a@example.test", null));

        var listB = await clientB.GetFromJsonAsync<ContactListResponse>("/api/contacts");

        Assert.Empty(listB!.Items);
    }

    [Fact]
    public async Task Delete_OtherAccountsContact_ReturnsNotFound()
    {
        var accountA = await SeedAccountAsync();
        var accountB = await SeedAccountAsync();
        var clientA = ClientFor(accountA);
        var clientB = ClientFor(accountB);
        var created = await clientA.PostAsJsonAsync("/api/contacts", new ContactRequest("owned@example.test", null));
        var contact = (await created.Content.ReadFromJsonAsync<ContactResponse>())!;

        var response = await clientB.DeleteAsync($"/api/contacts/{contact.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
            Username = "contact-test",
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
