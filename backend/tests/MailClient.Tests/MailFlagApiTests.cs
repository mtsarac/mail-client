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

public sealed class MailFlagApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Snooze_SetThenClear_RoundTrips()
    {
        var (accountId, mailId) = await SeedAccountWithMailAsync();
        var client = ClientFor(accountId);
        var until = DateTime.UtcNow.AddHours(2);

        var set = await client.PutAsJsonAsync($"/api/mails/{mailId}/snooze", new SnoozeRequest(until));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var map = await client.GetFromJsonAsync<SnoozeMapResponse>("/api/mails/snoozed");
        Assert.True(map!.Snoozed.ContainsKey(mailId));
        Assert.Equal(until, map.Snoozed[mailId], TimeSpan.FromSeconds(1));

        var cleared = await client.DeleteAsync($"/api/mails/{mailId}/snooze");
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        var mapAfter = await client.GetFromJsonAsync<SnoozeMapResponse>("/api/mails/snoozed");
        Assert.Empty(mapAfter!.Snoozed);
    }

    [Fact]
    public async Task Snooze_UnknownOrOtherAccountMail_Returns404()
    {
        var (accountId, _) = await SeedAccountWithMailAsync();
        var (_, otherMailId) = await SeedAccountWithMailAsync();
        var client = ClientFor(accountId);

        var response = await client.PutAsJsonAsync($"/api/mails/{otherMailId}/snooze", new SnoozeRequest(DateTime.UtcNow.AddHours(1)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unsnooze_AlreadyUnsnoozed_IsIdempotent()
    {
        var (accountId, mailId) = await SeedAccountWithMailAsync();
        var client = ClientFor(accountId);

        var response = await client.DeleteAsync($"/api/mails/{mailId}/snooze");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Pin_EnforcesCapOfThreePerAccount_PerItemResults()
    {
        var (accountId, mailIds) = await SeedAccountWithMailsAsync(4);
        var client = ClientFor(accountId);

        var response = await client.PostAsJsonAsync("/api/mails/pinned", new SetPinnedRequest(mailIds, true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<SetPinnedResponse>())!;
        Assert.Equal(3, body.Results.Count(r => r.Success));
        var failed = Assert.Single(body.Results, r => !r.Success);
        Assert.Equal("pinned_limit_reached", failed.Code);

        var pinned = await client.GetFromJsonAsync<PinnedIdsResponse>("/api/mails/pinned");
        Assert.Equal(3, pinned!.MailIds.Count);
    }

    [Fact]
    public async Task Pin_ReapplyingAnAlreadyPinnedMail_DoesNotConsumeAnotherSlot()
    {
        var (accountId, mailIds) = await SeedAccountWithMailsAsync(3);
        var client = ClientFor(accountId);
        await client.PostAsJsonAsync("/api/mails/pinned", new SetPinnedRequest(mailIds, true));

        var response = await client.PostAsJsonAsync("/api/mails/pinned", new SetPinnedRequest([mailIds[0]], true));

        var body = (await response.Content.ReadFromJsonAsync<SetPinnedResponse>())!;
        Assert.True(Assert.Single(body.Results).Success);
        var pinned = await client.GetFromJsonAsync<PinnedIdsResponse>("/api/mails/pinned");
        Assert.Equal(3, pinned!.MailIds.Count);
    }

    [Fact]
    public async Task Unpin_FreesASlotForAnotherMail()
    {
        var (accountId, mailIds) = await SeedAccountWithMailsAsync(4);
        var client = ClientFor(accountId);
        await client.PostAsJsonAsync("/api/mails/pinned", new SetPinnedRequest(mailIds[..3], true));

        var unpin = await client.PostAsJsonAsync("/api/mails/pinned", new SetPinnedRequest([mailIds[0]], false));
        Assert.True((await unpin.Content.ReadFromJsonAsync<SetPinnedResponse>())!.Results[0].Success);

        var pinFourth = await client.PostAsJsonAsync("/api/mails/pinned", new SetPinnedRequest([mailIds[3]], true));
        Assert.True((await pinFourth.Content.ReadFromJsonAsync<SetPinnedResponse>())!.Results[0].Success);
    }

    private HttpClient ClientFor(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private async Task<(Guid AccountId, Guid MailId)> SeedAccountWithMailAsync()
    {
        var (accountId, mailIds) = await SeedAccountWithMailsAsync(1);
        return (accountId, mailIds[0]);
    }

    private async Task<(Guid AccountId, List<Guid> MailIds)> SeedAccountWithMailsAsync(int count)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var mailIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@example.test",
            NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
            Username = "flag-test",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailFolder { Id = folderId, MailAccountId = accountId, Name = "INBOX", FullName = "INBOX", IsAvailable = true });
        for (var i = 0; i < mailIds.Count; i++)
        {
            db.Mails.Add(new MailClient.Domain.Entities.Mail
            {
                Id = mailIds[i],
                MailAccountId = accountId,
                MailFolderId = folderId,
                Uid = (uint)(i + 1),
                Subject = $"Test {i}",
                FromAddress = "sender@example.test",
                ToAddress = "recipient@example.test",
                ReceivedAt = DateTime.UtcNow,
                InternalDate = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return (accountId, mailIds);
    }
}
