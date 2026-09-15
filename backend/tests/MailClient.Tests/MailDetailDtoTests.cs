using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class MailDetailDtoTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    private readonly AcceptingApiFactory _factory = factory;

    [Fact]
    public async Task Get_ReturnsSanitizedBodyWithParticipants()
    {
        var (client, accountId) = await ConnectAsync();
        var mailId = await SeedMailAsync(accountId, "<script>alert(1)</script><p>Hi</p><img src=\"http://evil.test/p.png\">", "sender@corp.example", "Sender");

        var response = await client.GetAsync($"/api/mails/{mailId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(body);
        var root = body!.RootElement;
        var bodyObj = root.GetProperty("body");
        Assert.True(bodyObj.GetProperty("hasRemoteContent").GetBoolean());
        Assert.Contains("evil.test", bodyObj.GetProperty("remoteContentHosts").EnumerateArray().Select(h => h.GetString()));
        Assert.DoesNotContain("<script", bodyObj.GetProperty("html").GetString()!, StringComparison.OrdinalIgnoreCase);
        var from = root.GetProperty("from");
        Assert.Equal(1, from.GetArrayLength());
        Assert.Equal("sender@corp.example", from[0].GetProperty("address").GetString());
        Assert.Equal("Sender", from[0].GetProperty("displayName").GetString());
        Assert.True(root.GetProperty("answered").GetBoolean());
        Assert.False(root.GetProperty("flagged").GetBoolean());
        Assert.False(root.GetProperty("draft").GetBoolean());
        Assert.False(root.GetProperty("deleted").GetBoolean());
        Assert.False(root.GetProperty("recent").GetBoolean());
        Assert.NotNull(root.GetProperty("sentAt").GetString());
        Assert.NotNull(root.GetProperty("receivedAt").GetString());
        Assert.NotNull(root.GetProperty("internalDate").GetString());
    }

    [Fact]
    public async Task Get_ForeignAccount_Returns404()
    {
        var (client1, accountId1) = await ConnectAsync();
        var (client2, _) = await ConnectAsync();
        var mailId = await SeedMailAsync(accountId1, "<p>Private</p>", "owner@corp.example", "Owner");

        var response = await client2.GetAsync($"/api/mails/{mailId}");

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

    private async Task<Guid> SeedMailAsync(Guid accountId, string bodyHtml, string fromAddress, string fromDisplayName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mailId = Guid.NewGuid();
        db.Mails.Add(new Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = Guid.NewGuid(),
            Uid = (uint)Random.Shared.Next(1, int.MaxValue),
            Subject = "Test",
            FromAddress = fromAddress,
            FromDisplayName = fromDisplayName,
            BodyHtml = bodyHtml,
            BodyText = "plain",
            IsRead = false,
            HasAttachments = false,
            Answered = true,
            Flagged = false,
            Draft = false,
            Deleted = false,
            Recent = false,
            SentAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            InternalDate = DateTime.UtcNow
        });
        db.Participants.Add(new MailParticipant
        {
            Id = Guid.NewGuid(),
            MailId = mailId,
            Type = ParticipantType.From,
            Address = fromAddress,
            NormalizedAddress = fromAddress.ToLowerInvariant(),
            DisplayName = fromDisplayName,
            SortOrder = 0
        });
        await db.SaveChangesAsync();
        return mailId;
    }
}
