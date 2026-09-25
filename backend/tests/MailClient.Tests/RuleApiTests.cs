using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class RuleApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Rules_AreAccountScoped_AndLegacyImportIsIdempotent()
    {
        var (first, firstFolder, firstLabel) = await SeedAsync();
        var (second, secondFolder, secondLabel) = await SeedAsync();
        using var client = ClientFor(first);
        using var other = ClientFor(second);
        var request = new RuleRequest("Newsletter", true, 2, "And",
            [new RuleCondition("senderDomain", "example.test"), new RuleCondition("hasAttachment", null)],
            [new RuleAction("addLabel", LabelId: firstLabel), new RuleAction("move", FolderId: firstFolder)], "old-rule-id");

        var created = await client.PostAsJsonAsync("/api/rules", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rule = (await created.Content.ReadFromJsonAsync<RuleResponse>())!;
        Assert.Equal(rule.Id, (await (await client.PostAsJsonAsync("/api/rules", request)).Content.ReadFromJsonAsync<RuleResponse>())!.Id);
        Assert.Empty((await other.GetFromJsonAsync<List<RuleResponse>>("/api/rules"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/rules/{rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/rules/{rule.Id}", request)).StatusCode);
        var wrongFolder = request with { Actions = [new RuleAction("move", FolderId: secondFolder)] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/rules", wrongFolder with { LegacyId = null })).StatusCode);
        var wrongLabel = request with { Actions = [new RuleAction("addLabel", LabelId: secondLabel)], LegacyId = null };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/rules", wrongLabel)).StatusCode);

        var edited = request with { Name = "Priority one", Priority = 1, LegacyId = null };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/rules/{rule.Id}", edited)).StatusCode);
        Assert.Equal("Priority one", (await client.GetFromJsonAsync<List<RuleResponse>>("/api/rules"))!.Single().Name);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/rules/{rule.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<RuleResponse>>("/api/rules"))!);
    }

    private HttpClient ClientFor(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private async Task<(Guid AccountId, Guid FolderId, Guid LabelId)> SeedAsync()
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var labelId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@example.test",
            NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
            Username = "rule-test",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailFolder { Id = folderId, MailAccountId = accountId, Name = "Archive", FullName = "Archive", IsAvailable = true });
        db.MailLabels.Add(new MailLabel { Id = labelId, MailAccountId = accountId, Name = "News" });
        await db.SaveChangesAsync();
        return (accountId, folderId, labelId);
    }
}
