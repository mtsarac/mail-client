using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class TrustedSenderApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    private const string RemoteImageHtml = """<p>Hi</p><img src="https://images.example/photo.jpg"><a href="javascript:alert(1)">x</a>""";

    [Fact]
    public async Task Add_NormalizesAndIsIdempotent_Delete_Removes()
    {
        var (client, _) = await ConnectAsync();

        var created = await client.PostAsJsonAsync("/api/trusted-senders", new { kind = "Sender", value = "  News@Corp.Example " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = await ReadAsync(created);
        Assert.Equal("news@corp.example", first.GetProperty("value").GetString());
        Assert.Equal("Sender", first.GetProperty("kind").GetString());

        var again = await client.PostAsJsonAsync("/api/trusted-senders", new { kind = "Sender", value = "news@corp.example" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(first.GetProperty("id").GetGuid(), (await ReadAsync(again)).GetProperty("id").GetGuid());

        var domain = await ReadAsync(await client.PostAsJsonAsync("/api/trusted-senders", new { kind = "Domain", value = "@Corp.Example" }));
        Assert.Equal("corp.example", domain.GetProperty("value").GetString());

        var list = await ReadAsync(await client.GetAsync("/api/trusted-senders"));
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/trusted-senders/{first.GetProperty("id").GetGuid()}")).StatusCode);
        var remaining = (await ReadAsync(await client.GetAsync("/api/trusted-senders"))).GetProperty("items");
        Assert.Equal("Domain", Assert.Single(remaining.EnumerateArray()).GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("Sender", "not-an-address")]
    [InlineData("Sender", "a@b@corp.example")]
    [InlineData("Domain", "localhost")]
    [InlineData("Domain", "bad domain.example")]
    [InlineData("Domain", "")]
    public async Task Add_InvalidValue_ReturnsValidationProblem(string kind, string value)
    {
        var (client, _) = await ConnectAsync();

        var response = await client.PostAsJsonAsync("/api/trusted-senders", new { kind, value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("value", out _));
    }

    [Fact]
    public async Task OtherAccountsEntry_IsInvisibleAndCannotBeDeleted()
    {
        var (clientA, _) = await ConnectAsync();
        var (clientB, _) = await ConnectAsync();
        var entry = await ReadAsync(await clientA.PostAsJsonAsync("/api/trusted-senders", new { kind = "Domain", value = "corp.example" }));

        Assert.Equal(0, (await ReadAsync(await clientB.GetAsync("/api/trusted-senders"))).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"/api/trusted-senders/{entry.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [Fact]
    public async Task MailDetail_LoadsOrdinaryImagesByDefault()
    {
        var (client, accountId) = await ConnectAsync();
        var bySender = await SeedMailAsync(accountId, "news@corp.example", MailFolderType.Inbox);
        var byDomain = await SeedMailAsync(accountId, "billing@corp.example", MailFolderType.Inbox);
        var untrusted = await SeedMailAsync(accountId, "someone@other.example", MailFolderType.Inbox);

        Assert.True(await RemoteImagesAllowedAsync(client, bySender));
        Assert.True(await RemoteImagesAllowedAsync(client, byDomain));
        Assert.True(await RemoteImagesAllowedAsync(client, untrusted));
        var html = await BodyHtmlAsync(client, bySender);
        Assert.Contains("src=\"https://images.example/photo.jpg\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MailDetail_TrustedSender_InJunkOrFailingDmarc_StaysBlocked()
    {
        var (client, accountId) = await ConnectAsync();
        await client.PostAsJsonAsync("/api/trusted-senders", new { kind = "Domain", value = "corp.example" });
        var junk = await SeedMailAsync(accountId, "news@corp.example", MailFolderType.Junk);
        var spoofed = await SeedMailAsync(accountId, "news@corp.example", MailFolderType.Inbox,
            "mx.example.net; spf=fail smtp.mailfrom=corp.example; dmarc=fail header.from=corp.example");
        var passing = await SeedMailAsync(accountId, "news@corp.example", MailFolderType.Inbox,
            "mx.example.net; spf=pass smtp.mailfrom=corp.example; dmarc=pass header.from=corp.example");

        Assert.False(await RemoteImagesAllowedAsync(client, junk));
        Assert.False(await RemoteImagesAllowedAsync(client, spoofed));
        Assert.True(await RemoteImagesAllowedAsync(client, passing));
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<bool> RemoteImagesAllowedAsync(HttpClient client, Guid mailId) =>
        (await ReadAsync(await client.GetAsync($"/api/mails/{mailId}"))).GetProperty("body").GetProperty("remoteImagesAllowed").GetBoolean();

    private static async Task<string> BodyHtmlAsync(HttpClient client, Guid mailId) =>
        (await ReadAsync(await client.GetAsync($"/api/mails/{mailId}"))).GetProperty("body").GetProperty("html").GetString()!;

    private async Task<(HttpClient Client, Guid AccountId)> ConnectAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/connect-manual", ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", $"{Guid.NewGuid():N}@mail.test.invalid"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<JsonDocument>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.RootElement.GetProperty("accessToken").GetString());
        return (client, tokens.RootElement.GetProperty("mailAccountId").GetGuid());
    }

    private async Task<Guid> SeedMailAsync(Guid accountId, string fromAddress, MailFolderType folderType, string? authenticationResults = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var folderId = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = folderType.ToString(),
            FullName = $"{folderType}-{folderId:N}",
            FolderType = folderType
        });
        var mailId = Guid.NewGuid();
        db.Mails.Add(new Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = folderId,
            Uid = (uint)Random.Shared.Next(1, int.MaxValue),
            Subject = "Newsletter",
            FromAddress = fromAddress,
            FromDisplayName = "Sender",
            BodyHtml = RemoteImageHtml,
            BodyText = "plain",
            SentAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            InternalDate = DateTime.UtcNow
        });
        if (authenticationResults is not null)
            db.MailHeaders.Add(new MailHeader { Id = Guid.NewGuid(), MailId = mailId, Name = "Authentication-Results", Value = authenticationResults });
        await db.SaveChangesAsync();
        return mailId;
    }
}
