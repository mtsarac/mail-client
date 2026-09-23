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

public sealed class ConversationApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    private readonly AcceptingApiFactory _factory = factory;

    [Fact]
    public async Task List_GroupsThreeMailsIntoTwoConversations()
    {
        var (client, accountId) = await ConnectAsync();
        await SeedConversationAsync(accountId, convId: Guid.NewGuid(), subject: "Quarterly Report", mails:
        [
            new SeedMail("sender@corp.example", "Alice", SentAt: DateTime.UtcNow.AddHours(-2), IsRead: true, HasAttachments: true),
            new SeedMail("bob@corp.example", "Bob", SentAt: DateTime.UtcNow.AddHours(-1), IsRead: false, HasAttachments: false),
            new SeedMail("sender@corp.example", "Alice", SentAt: DateTime.UtcNow.AddMinutes(-30), IsRead: true, HasAttachments: false)
        ]);
        await SeedConversationAsync(accountId, convId: Guid.NewGuid(), subject: "Lunch Plans", mails:
        [
            new SeedMail("carol@corp.example", "Carol", SentAt: DateTime.UtcNow, IsRead: false, HasAttachments: false)
        ]);

        var response = await client.GetAsync("/api/conversations?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(body);
        var root = body!.RootElement;
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var quarterly = items.EnumerateArray().First(i => i.GetProperty("subject").GetString() == "Quarterly Report");
        Assert.Equal(3, quarterly.GetProperty("messageCount").GetInt32());
        Assert.Equal(1, quarterly.GetProperty("unreadCount").GetInt32());
        Assert.True(quarterly.GetProperty("hasAttachments").GetBoolean());
        Assert.Equal(["Alice", "Bob"], quarterly.GetProperty("participants").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Detail_ReturnsMessagesOrderedBySentAt()
    {
        var (client, accountId) = await ConnectAsync();
        var convId = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddHours(-3);
        var t1 = t0.AddHours(1);
        var t2 = t1.AddHours(1);
        await SeedConversationAsync(accountId, convId, "Thread", mails:
        [
            new SeedMail("a@corp.example", "A", SentAt: t2, IsRead: false, HasAttachments: false),
            new SeedMail("b@corp.example", "B", SentAt: t0, IsRead: false, HasAttachments: false),
            new SeedMail("c@corp.example", "C", SentAt: t1, IsRead: false, HasAttachments: false)
        ]);

        var response = await client.GetAsync($"/api/conversations/{convId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(body);
        var messages = body!.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var sentAts = messages.EnumerateArray().Select(m => DateTime.Parse(m.GetProperty("sentAt").GetString()!)).ToList();
        Assert.True(sentAts[0] <= sentAts[1] && sentAts[1] <= sentAts[2], "Messages must be ordered by SentAt ascending");
    }

    [Fact]
    public async Task Detail_UnknownId_Returns404()
    {
        var (client, _) = await ConnectAsync();

        var response = await client.GetAsync($"/api/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Detail_ForeignAccount_Returns404()
    {
        var (client1, accountId1) = await ConnectAsync();
        var (client2, _) = await ConnectAsync();
        var convId = Guid.NewGuid();
        await SeedConversationAsync(accountId1, convId, "Private", mails:
        [
            new SeedMail("owner@corp.example", "Owner", SentAt: DateTime.UtcNow, IsRead: false, HasAttachments: false)
        ]);

        var response = await client2.GetAsync($"/api/conversations/{convId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(HttpClient Client, Guid AccountId)> ConnectAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", $"{Guid.NewGuid():N}@mail.test.invalid"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<JsonDocument>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.RootElement.GetProperty("accessToken").GetString());
        return (client, tokens.RootElement.GetProperty("mailAccountId").GetGuid());
    }

    private async Task SeedConversationAsync(Guid accountId, Guid convId, string subject, SeedMail[] mails)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Conversations.Add(new Conversation
        {
            Id = convId,
            MailAccountId = accountId,
            NormalizedSubject = subject,
            StartedAt = mails.Min(m => m.SentAt),
            LastMessageAt = mails.Max(m => m.SentAt)
        });
        foreach (var mail in mails)
        {
            var mailId = Guid.NewGuid();
            db.Mails.Add(new Mail
            {
                Id = mailId,
                MailAccountId = accountId,
                MailFolderId = Guid.NewGuid(),
                ConversationId = convId,
                Uid = (uint)Random.Shared.Next(1, int.MaxValue),
                Subject = subject,
                FromAddress = mail.FromAddress,
                FromDisplayName = mail.FromDisplayName,
                IsRead = mail.IsRead,
                HasAttachments = mail.HasAttachments,
                SentAt = mail.SentAt,
                ReceivedAt = mail.SentAt,
                InternalDate = mail.SentAt
            });
            db.Participants.Add(new MailParticipant
            {
                Id = Guid.NewGuid(),
                MailId = mailId,
                Type = ParticipantType.From,
                Address = mail.FromAddress,
                NormalizedAddress = mail.FromAddress.ToLowerInvariant(),
                DisplayName = mail.FromDisplayName,
                SortOrder = 0
            });
        }
        await db.SaveChangesAsync();
    }

    private sealed record SeedMail(string FromAddress, string FromDisplayName, DateTime SentAt, bool IsRead, bool HasAttachments);
}
