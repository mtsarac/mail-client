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

public sealed class MailListApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    private readonly AcceptingApiFactory _factory = factory;

    [Fact]
    public async Task List_IsScopedToOwningAccount()
    {
        var (client, _) = await ConnectAsync();
        var (_, otherAccountId) = await ConnectAsync();
        await SeedAsync(otherAccountId, "other-mail");

        var response = await client.GetAsync("/api/mails");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadAsync(response);
        Assert.DoesNotContain(result.Items, item => item.Subject == "other-mail");
    }

    [Fact]
    public async Task List_FiltersByFolderReadAndAttachments()
    {
        var (client, accountId) = await ConnectAsync();
        var folder = Guid.NewGuid();
        await SeedAsync(accountId, "unread-attach", folder, isRead: false, hasAttachments: true);
        await SeedAsync(accountId, "read-plain", folder, isRead: true, hasAttachments: false);

        var unreadWithAttachments = await ReadAsync(await client.GetAsync($"/api/mails?folderId={folder}&isRead=false&hasAttachments=true"));
        var item = Assert.Single(unreadWithAttachments.Items);
        Assert.Equal("unread-attach", item.Subject);
        Assert.Equal(folder, item.FolderId);

        var read = await ReadAsync(await client.GetAsync($"/api/mails?folderId={folder}&isRead=true"));
        Assert.Equal("read-plain", Assert.Single(read.Items).Subject);
    }

    [Fact]
    public async Task List_SearchMatchesSubjectAndSender()
    {
        var (client, accountId) = await ConnectAsync();
        await SeedAsync(accountId, "Quarterly Report", fromAddress: "boss@corp.example");
        await SeedAsync(accountId, "Lunch", fromAddress: "friend@corp.example");

        var bySubject = await ReadAsync(await client.GetAsync("/api/mails?search=Quarterly"));
        Assert.Equal("Quarterly Report", Assert.Single(bySubject.Items).Subject);

        var bySender = await ReadAsync(await client.GetAsync("/api/mails?search=boss@corp"));
        Assert.Equal("Quarterly Report", Assert.Single(bySender.Items).Subject);
    }

    [Fact]
    public async Task List_Paginates_WithTotal_AndCapsPageSize()
    {
        var (client, accountId) = await ConnectAsync();
        for (var index = 0; index < 5; index++)
            await SeedAsync(accountId, $"page-{index}");

        var first = await ReadAsync(await client.GetAsync("/api/mails?page=1&pageSize=2"));
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.PageSize);
        Assert.True(first.Total >= 5);

        var second = await ReadAsync(await client.GetAsync("/api/mails?page=2&pageSize=2"));
        Assert.Equal(2, second.Items.Count);
        Assert.DoesNotContain(second.Items, item => item.Id == first.Items[0].Id);

        var capped = await ReadAsync(await client.GetAsync("/api/mails?pageSize=5000"));
        Assert.Equal(100, capped.PageSize);

        var clamped = await ReadAsync(await client.GetAsync("/api/mails?page=0&pageSize=0"));
        Assert.Equal(1, clamped.Page);
        Assert.Equal(50, clamped.PageSize);
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

    private async Task SeedAsync(Guid accountId, string subject, Guid? folderId = null, bool isRead = false, bool hasAttachments = false, string? fromAddress = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Mails.Add(new Mail
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            MailFolderId = folderId ?? Guid.NewGuid(),
            Uid = (uint)Random.Shared.Next(1, int.MaxValue),
            Subject = subject,
            FromAddress = fromAddress ?? "sender@corp.example",
            IsRead = isRead,
            HasAttachments = hasAttachments,
            ReceivedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<MailClient.Application.Mail.MailListResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<MailClient.Application.Mail.MailListResponse>())!;
}
