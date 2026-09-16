using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class ComposeContextApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    private readonly AcceptingApiFactory _factory = factory;

    [Fact]
    public async Task ReplyContext_IsScopedToOwningAccount()
    {
        var owner = await ConnectAsync("owner@example.test");
        var other = await ConnectAsync("other@example.test");
        var mailId = await SeedMailAsync(owner.AccountId);

        var response = await other.Client.GetAsync($"/api/mails/{mailId}/compose/reply");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReplyAllContext_ReturnsCalculatedRecipientsAndNoBcc()
    {
        var owner = await ConnectAsync("me@example.test");
        var mailId = await SeedMailAsync(owner.AccountId, includeParticipants: true);

        var response = await owner.Client.GetAsync($"/api/mails/{mailId}/compose/reply-all");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(mailId, body!.RootElement.GetProperty("sourceMailId").GetGuid());
        Assert.Equal("Re: Hello", body.RootElement.GetProperty("suggestedSubject").GetString());
        Assert.Equal("source@example.test", body.RootElement.GetProperty("inReplyToMessageId").GetString());
        Assert.Equal("prior@example.test source@example.test", body.RootElement.GetProperty("references").GetString());
        var to = body.RootElement.GetProperty("to").EnumerateArray().Select(x => x.GetProperty("address").GetString()).ToList();
        var cc = body.RootElement.GetProperty("cc").EnumerateArray().Select(x => x.GetProperty("address").GetString()).ToList();
        Assert.Equal(["reply@example.test", "to@example.test"], to);
        Assert.Equal(["cc@example.test"], cc);
        Assert.False(body.RootElement.TryGetProperty("bcc", out _));
    }

    [Fact]
    public async Task ForwardContext_ReturnsMetadataAndAttachmentMetadataOnly()
    {
        var owner = await ConnectAsync("me@example.test");
        var mailId = await SeedMailAsync(owner.AccountId, includeAttachment: true);

        var response = await owner.Client.GetAsync($"/api/mails/{mailId}/compose/forward");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Fwd: Hello", body!.RootElement.GetProperty("suggestedSubject").GetString());
        Assert.Equal("sender@example.test", body.RootElement.GetProperty("originalFrom").GetString());
        Assert.Equal("Hello", body.RootElement.GetProperty("originalSubject").GetString());
        var attachment = Assert.Single(body.RootElement.GetProperty("attachments").EnumerateArray());
        Assert.Equal("note.txt", attachment.GetProperty("fileName").GetString());
    }

    private async Task<(HttpClient Client, Guid AccountId)> ConnectAsync(string email)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", email));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<JsonDocument>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.RootElement.GetProperty("accessToken").GetString());
        return (client, tokens.RootElement.GetProperty("mailAccountId").GetGuid());
    }

    private async Task<Guid> SeedMailAsync(Guid accountId, bool includeParticipants = false, bool includeAttachment = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mailId = Guid.NewGuid();
        var mail = new Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = Guid.NewGuid(),
            MessageId = "source@example.test",
            References = "prior@example.test",
            Subject = "Hello",
            FromAddress = "sender@example.test",
            SentAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        };
        if (includeParticipants)
        {
            mail.Participants =
            [
                Participant(mailId, ParticipantType.From, "sender@example.test", 0),
                Participant(mailId, ParticipantType.ReplyTo, "reply@example.test", 0),
                Participant(mailId, ParticipantType.To, "me@example.test", 0),
                Participant(mailId, ParticipantType.To, "to@example.test", 1),
                Participant(mailId, ParticipantType.Cc, "reply@example.test", 0),
                Participant(mailId, ParticipantType.Cc, "cc@example.test", 1),
                Participant(mailId, ParticipantType.Bcc, "secret@example.test", 0)
            ];
        }
        if (includeAttachment)
            mail.Attachments.Add(new Attachment { Id = Guid.NewGuid(), MailAccountId = accountId, MailId = mailId, FileName = "note.txt", ContentType = "text/plain", SizeBytes = 4 });
        db.Mails.Add(mail);
        await db.SaveChangesAsync();
        return mailId;
    }

    private static MailParticipant Participant(Guid mailId, ParticipantType type, string address, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        MailId = mailId,
        Type = type,
        Address = address,
        NormalizedAddress = address.ToUpperInvariant(),
        SortOrder = sortOrder
    };
}
