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

public sealed class LabelApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Create_List_Update_Delete_RoundTrips()
    {
        var client = await ClientForNewAccountAsync();

        var created = await client.PostAsJsonAsync("/api/labels", new LabelRequest("Work", unchecked((int)0xFF3E7CB1)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var label = (await created.Content.ReadFromJsonAsync<LabelResponse>())!;
        Assert.Equal("Work", label.Name);

        var list = await client.GetFromJsonAsync<LabelListResponse>("/api/labels");
        Assert.Single(list!.Items);

        var updated = await client.PutAsJsonAsync($"/api/labels/{label.Id}", new LabelRequest("Personal", unchecked((int)0xFF2E8B6E)));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedLabel = (await updated.Content.ReadFromJsonAsync<LabelResponse>())!;
        Assert.Equal(label.Id, updatedLabel.Id);
        Assert.Equal("Personal", updatedLabel.Name);

        var deleted = await client.DeleteAsync($"/api/labels/{label.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var afterDelete = await client.GetFromJsonAsync<LabelListResponse>("/api/labels");
        Assert.Empty(afterDelete!.Items);
    }

    [Fact]
    public async Task Create_DuplicateNameCaseInsensitive_Returns409()
    {
        var client = await ClientForNewAccountAsync();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/labels", new LabelRequest("Finans", 1))).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/labels", new LabelRequest("finans", 2));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Assignments_AreScopedPerAccount_AndDeletingLabelStripsThem()
    {
        var (accountId, mailId) = await SeedAccountWithMailAsync();
        var otherAccountId = (await SeedAccountWithMailAsync()).AccountId;
        var client = ClientFor(accountId);
        var otherClient = ClientFor(otherAccountId);

        var label = (await (await client.PostAsJsonAsync("/api/labels", new LabelRequest("İş", 1))).Content.ReadFromJsonAsync<LabelResponse>())!;

        var assign = await client.PostAsJsonAsync("/api/labels/assignments", new LabelAssignmentRequest([mailId], [label.Id]));
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

        var map = await client.GetFromJsonAsync<LabelAssignmentsResponse>("/api/labels/assignments");
        Assert.Equal([label.Id], map!.Assignments[mailId]);

        // The other account's assignment map never sees this account's mail/label.
        var otherMap = await otherClient.GetFromJsonAsync<LabelAssignmentsResponse>("/api/labels/assignments");
        Assert.Empty(otherMap!.Assignments);

        var deleted = await client.DeleteAsync($"/api/labels/{label.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var mapAfterDelete = await client.GetFromJsonAsync<LabelAssignmentsResponse>("/api/labels/assignments");
        Assert.Empty(mapAfterDelete!.Assignments);
    }

    [Fact]
    public async Task Assignments_Remove_StripsOnlyRequestedPairs()
    {
        var (accountId, mailId) = await SeedAccountWithMailAsync();
        var client = ClientFor(accountId);
        var labelA = (await (await client.PostAsJsonAsync("/api/labels", new LabelRequest("A", 1))).Content.ReadFromJsonAsync<LabelResponse>())!;
        var labelB = (await (await client.PostAsJsonAsync("/api/labels", new LabelRequest("B", 2))).Content.ReadFromJsonAsync<LabelResponse>())!;
        await client.PostAsJsonAsync("/api/labels/assignments", new LabelAssignmentRequest([mailId], [labelA.Id, labelB.Id]));

        var remove = await client.PostAsJsonAsync("/api/labels/assignments/remove", new LabelAssignmentRequest([mailId], [labelA.Id]));

        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        var map = await client.GetFromJsonAsync<LabelAssignmentsResponse>("/api/labels/assignments");
        Assert.Equal([labelB.Id], map!.Assignments[mailId]);
    }

    private HttpClient ClientFor(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private async Task<HttpClient> ClientForNewAccountAsync() => ClientFor((await SeedAccountWithMailAsync()).AccountId);

    private async Task<(Guid AccountId, Guid MailId)> SeedAccountWithMailAsync()
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@example.test",
            NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
            Username = "label-test",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailFolder { Id = folderId, MailAccountId = accountId, Name = "INBOX", FullName = "INBOX", IsAvailable = true });
        db.Mails.Add(new MailClient.Domain.Entities.Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = folderId,
            Uid = 1,
            Subject = "Test",
            FromAddress = "sender@example.test",
            ToAddress = "recipient@example.test",
            ReceivedAt = DateTime.UtcNow,
            InternalDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (accountId, mailId);
    }
}
