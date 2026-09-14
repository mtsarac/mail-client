using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Api.Tests;

public sealed class MailEndpointsTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Anonymous_ListMail_ReturnsUnauthorized()
    {
        var client = CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/mails?page=1&pageSize=30")).StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOwnMailWithoutBodies()
    {
        var (userId, token) = await SeedUserAsync(UniqueEmail("reader"), "reader-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);
        var folderId = await SeedFolderAsync(accountId, MailFolderType.Inbox);
        await SeedMailAsync(userId, accountId, folderId, "hello");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/mails?page=1&pageSize=30");
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        var item = list.GetProperty("items")[0];
        Assert.Equal("hello", item.GetProperty("subject").GetString());
        Assert.False(item.TryGetProperty("bodyHtml", out _));
        Assert.False(item.TryGetProperty("bodyText", out _));
        Assert.False(item.TryGetProperty("storagePath", out _));
    }

    [Fact]
    public async Task OtherUser_AndAdmin_CannotSeeMail()
    {
        var (userId, token) = await SeedUserAsync(UniqueEmail("owner"), "owner-password-1");
        var ownerClient = CreateClient();
        Authenticate(ownerClient, token);
        var accountId = await CreateAccountAsync(ownerClient);
        var folderId = await SeedFolderAsync(accountId, MailFolderType.Inbox);
        var mailId = await SeedMailAsync(userId, accountId, folderId, "private");

        var (_, otherToken) = await SeedUserAsync(UniqueEmail("other"), "other-password-1");
        var otherClient = CreateClient();
        Authenticate(otherClient, otherToken);
        var otherList = await otherClient.GetFromJsonAsync<JsonElement>("/api/mails?page=1&pageSize=30");
        Assert.Equal(0, otherList.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/mails/{mailId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.GetAsync($"/api/mails/{mailId}/attachments/{Guid.NewGuid():N}")).StatusCode);

        var (_, adminToken) = await SeedUserAsync(UniqueEmail("admin"), "admin-password-1", UserRole.Admin);
        var adminClient = CreateClient();
        Authenticate(adminClient, adminToken);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.GetAsync($"/api/mails/{mailId}")).StatusCode);
    }

    [Fact]
    public async Task List_FiltersAndPagination()
    {
        var (userId, token) = await SeedUserAsync(UniqueEmail("filter"), "filter-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);
        var inboxId = await SeedFolderAsync(accountId, MailFolderType.Inbox);
        var sentId = await SeedFolderAsync(accountId, MailFolderType.Sent, "Sent");
        await SeedMailAsync(userId, accountId, inboxId, "inbox-one");
        await SeedMailAsync(userId, accountId, sentId, "sent-one");

        var inbox = await client.GetFromJsonAsync<JsonElement>("/api/mails?folderType=Inbox&page=1&pageSize=30");
        Assert.Equal(1, inbox.GetProperty("totalCount").GetInt32());

        var sent = await client.GetFromJsonAsync<JsonElement>($"/api/mails?folderId={sentId}&page=1&pageSize=30");
        Assert.Equal(1, sent.GetProperty("totalCount").GetInt32());
        Assert.Equal("sent-one", sent.GetProperty("items")[0].GetProperty("subject").GetString());

        var page2 = await client.GetFromJsonAsync<JsonElement>("/api/mails?page=2&pageSize=1");
        Assert.Equal(2, page2.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/mails?page=0&pageSize=30")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/mails?folderType=Nope&page=1&pageSize=30")).StatusCode);
    }

    [Fact]
    public async Task Detail_ExposesBodyAndAttachmentMetadata_WithoutStoragePath()
    {
        var (userId, token) = await SeedUserAsync(UniqueEmail("detail"), "detail-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);
        var folderId = await SeedFolderAsync(accountId, MailFolderType.Inbox);
        var mailId = await SeedMailAsync(userId, accountId, folderId, "with-body", bodyHtml: "<p>hi</p>");
        await SeedAttachmentAsync(mailId, "note.txt", "text/plain", "attachment-bytes"u8.ToArray());

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/mails/{mailId}");
        Assert.Equal("<p>hi</p>", detail.GetProperty("bodyHtml").GetString());
        var attachments = detail.GetProperty("attachments");
        Assert.Equal(1, attachments.GetArrayLength());
        Assert.Equal("note.txt", attachments[0].GetProperty("fileName").GetString());
        Assert.False(attachments[0].TryGetProperty("storagePath", out _));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/mails/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Attachment_DownloadsBytesWithStoredContentType()
    {
        var (userId, token) = await SeedUserAsync(UniqueEmail("dl"), "dl-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateAccountAsync(client);
        var folderId = await SeedFolderAsync(accountId, MailFolderType.Inbox);
        var mailId = await SeedMailAsync(userId, accountId, folderId, "download");
        var attachmentId = await SeedAttachmentAsync(mailId, "data.bin", "application/octet-stream", [1, 2, 3, 4]);

        var response = await client.GetAsync($"/api/mails/{mailId}/attachments/{attachmentId}");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PatchRead_UnreachableServer_ReturnsBadGateway_AndOtherUserGetsNotFound()
    {
        var (userId, token) = await SeedUserAsync(UniqueEmail("patch"), "patch-password-1");
        var client = CreateClient();
        Authenticate(client, token);
        var accountId = await CreateUnreachableAccountAsync(client);
        var folderId = await SeedFolderAsync(accountId, MailFolderType.Inbox);
        var mailId = await SeedMailAsync(userId, accountId, folderId, "flag-me");

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/mails/{mailId}/read")
        {
            Content = JsonContent.Create(new { isRead = true })
        };
        var failed = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);

        var (_, otherToken) = await SeedUserAsync(UniqueEmail("patch-other"), "other-password-1");
        var otherClient = CreateClient();
        Authenticate(otherClient, otherToken);
        var otherPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/mails/{mailId}/read")
        {
            Content = JsonContent.Create(new { isRead = true })
        };
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.SendAsync(otherPatch)).StatusCode);
    }

    private async Task<Guid> SeedFolderAsync(Guid accountId, MailFolderType type, string name = "INBOX")
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var folderId = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = name,
            FullName = name,
            FolderType = type,
            IsSyncEnabled = true
        });
        await db.SaveChangesAsync();
        return folderId;
    }

    private async Task<Guid> SeedMailAsync(
        Guid userId, Guid accountId, Guid folderId, string subject, string bodyHtml = "")
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mailId = Guid.NewGuid();
        db.Mails.Add(new Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = folderId,
            Uid = (uint)Random.Shared.Next(1, 1000000),
            UidValidity = 100,
            MessageId = $"{mailId:N}@example.test",
            Subject = subject,
            FromAddress = "sender@example.test",
            FromDisplayName = "Sender",
            ToAddress = "me@example.test",
            BodyHtml = bodyHtml,
            BodyText = "body",
            ReceivedAt = DateTime.UtcNow,
            IsRead = false
        });
        await db.SaveChangesAsync();
        return mailId;
    }

    private async Task<Guid> SeedAttachmentAsync(Guid mailId, string fileName, string contentType, byte[] bytes)
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<AppDbContext>();
        var storage = provider.GetRequiredService<IFileStorage>();
        var mail = await db.Mails.SingleAsync(item => item.Id == mailId);
        var attachmentId = Guid.NewGuid();
        var stored = await storage.SaveAsync(
            mail.MailAccountId, mailId, attachmentId,
            (destination, ct) => new MemoryStream(bytes).CopyToAsync(destination, ct),
            bytes.Length + 16, CancellationToken.None);
        db.Attachments.Add(new Attachment
        {
            Id = attachmentId,
            MailId = mailId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = stored.SizeBytes,
            StoragePath = stored.RelativePath,
            IsInline = false,
            ContentId = string.Empty
        });
        await db.SaveChangesAsync();
        return attachmentId;
    }

    private static async Task<Guid> CreateUnreachableAccountAsync(HttpClient client)
    {
        var payload = new
        {
            emailAddress = UniqueEmail("unreachable"),
            displayName = "Unreachable",
            username = "nobody",
            password = "mailbox-secret-1",
            imapHost = "imap.invalid",
            imapPort = 993,
            imapSecurity = "SslOnConnect",
            smtpHost = "smtp.invalid",
            smtpPort = 587,
            smtpSecurity = "StartTls",
            saveSentCopy = true
        };
        var response = await client.PostAsJsonAsync("/api/mail-accounts", payload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }
}
