using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MimeKit;

namespace MailClient.Tests;

public sealed class FakeRemoteFolderManager : IRemoteFolderManager
{
    public List<string> Calls { get; } = [];
    public RemoteFolderOutcome NextOutcome { get; set; } = RemoteFolderOutcome.Succeeded;

    public Task<RemoteFolderResult> CreateAsync(MailAccount account, string? parentFullName, string name, CancellationToken cancellationToken)
    {
        Calls.Add($"create {account.Id} {parentFullName} {name}");
        var fullName = parentFullName is null ? name : $"{parentFullName}/{name}";
        return Task.FromResult(Result(new DiscoveredMailFolder(name, fullName, MailFolderType.Custom, 7, "/")));
    }

    public Task<RemoteFolderResult> RenameAsync(MailAccount account, string fullName, string name, CancellationToken cancellationToken)
    {
        Calls.Add($"rename {account.Id} {fullName} {name}");
        var index = fullName.LastIndexOf('/');
        var renamed = index < 0 ? name : $"{fullName[..index]}/{name}";
        return Task.FromResult(Result(new DiscoveredMailFolder(name, renamed, MailFolderType.Custom, 7, "/")));
    }

    public Task<RemoteFolderResult> MoveAsync(MailAccount account, string fullName, string? parentFullName, CancellationToken cancellationToken)
    {
        Calls.Add($"move {account.Id} {fullName} {parentFullName}");
        var name = fullName[(fullName.LastIndexOf('/') + 1)..];
        var moved = parentFullName is null ? name : $"{parentFullName}/{name}";
        return Task.FromResult(Result(new DiscoveredMailFolder(name, moved, MailFolderType.Custom, 7, "/")));
    }

    public Task<RemoteFolderResult> DeleteAsync(MailAccount account, string fullName, CancellationToken cancellationToken)
    {
        Calls.Add($"delete {account.Id} {fullName}");
        return Task.FromResult(Result(null));
    }

    private RemoteFolderResult Result(DiscoveredMailFolder? folder)
    {
        var outcome = NextOutcome;
        NextOutcome = RemoteFolderOutcome.Succeeded;
        return outcome == RemoteFolderOutcome.Succeeded ? new(outcome, folder) : new(outcome);
    }
}

public sealed class FolderManagementApiFactory : MailClientApiFactory
{
    public FakeRemoteFolderManager Remote { get; } = new();
    public FakeFileStorage Storage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRemoteFolderManager>();
            services.AddSingleton<IRemoteFolderManager>(Remote);
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(Storage);
        });
    }
}

public sealed class FolderManagementApiTests(FolderManagementApiFactory factory) : IClassFixture<FolderManagementApiFactory>
{
    [Fact]
    public async Task CreateRename_BuildsServerHierarchy_KeepsChildIds_AndStaysInsideTheAccount()
    {
        var (accountId, inbox, _) = await SeedAsync();
        var (otherAccountId, otherInbox, _) = await SeedAsync();
        var client = Client(accountId);

        var root = await ReadFolderAsync(await client.PostAsJsonAsync("/api/folders", new { name = "  Projeler  " }), HttpStatusCode.Created);
        Assert.Equal("Projeler", root.GetProperty("fullName").GetString());
        var rootId = root.GetProperty("id").GetGuid();
        var child = await ReadFolderAsync(await client.PostAsJsonAsync("/api/folders", new { name = "Mobil", parentId = rootId }), HttpStatusCode.Created);
        Assert.Equal("Projeler/Mobil", child.GetProperty("fullName").GetString());
        Assert.Equal(rootId, child.GetProperty("parentId").GetGuid());
        Assert.Equal("/", child.GetProperty("delimiter").GetString());

        await ExpectProblemAsync(await client.PostAsJsonAsync("/api/folders", new { name = "A/B", parentId = rootId }), HttpStatusCode.BadRequest, "invalid_folder_name");
        await ExpectProblemAsync(await client.PostAsJsonAsync("/api/folders", new { name = "   " }), HttpStatusCode.BadRequest, "invalid_folder_name");
        await ExpectProblemAsync(await client.PostAsJsonAsync("/api/folders", new { name = "X", parentId = otherInbox }), HttpStatusCode.NotFound, "mail_folder_not_found");
        factory.Remote.NextOutcome = RemoteFolderOutcome.AlreadyExists;
        await ExpectProblemAsync(await client.PostAsJsonAsync("/api/folders", new { name = "Mobil", parentId = rootId }), HttpStatusCode.Conflict, "mail_folder_exists");

        var renamed = await ReadFolderAsync(await client.PatchAsJsonAsync($"/api/folders/{rootId}", new { name = "İşler" }), HttpStatusCode.OK);
        Assert.Equal("İşler", renamed.GetProperty("fullName").GetString());
        var folders = (await client.GetFromJsonAsync<JsonElement>("/api/folders")).EnumerateArray().ToList();
        var movedChild = folders.Single(f => f.GetProperty("id").GetGuid() == child.GetProperty("id").GetGuid());
        Assert.Equal("İşler/Mobil", movedChild.GetProperty("fullName").GetString());
        Assert.Equal(rootId, movedChild.GetProperty("parentId").GetGuid());

        await ExpectProblemAsync(await client.PatchAsJsonAsync($"/api/folders/{inbox}", new { name = "Gelen" }), HttpStatusCode.UnprocessableEntity, "mail_folder_protected");
        await ExpectProblemAsync(await client.PatchAsJsonAsync($"/api/folders/{otherInbox}", new { name = "Gelen" }), HttpStatusCode.NotFound, "mail_folder_not_found");
        await ExpectProblemAsync(await client.DeleteAsync($"/api/folders/{rootId}"), HttpStatusCode.Conflict, "mail_folder_has_children");
        Assert.DoesNotContain(factory.Remote.Calls, call => call.Contains(otherAccountId.ToString()));
        Assert.DoesNotContain(factory.Remote.Calls, call => call.StartsWith($"delete {accountId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_ReparentsRemoteFolders_KeepsCachedMailAndDescendantIds_AndRejectsUnsafeRequests()
    {
        var (accountId, inbox, custom) = await SeedAsync();
        var (_, otherInbox, _) = await SeedAsync();
        var client = Client(accountId);
        var child = await ReadFolderAsync(await client.PostAsJsonAsync("/api/folders", new { name = "Child", parentId = custom }), HttpStatusCode.Created);
        var childId = child.GetProperty("id").GetGuid();
        var mailId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Mails.Add(new Domain.Entities.Mail { Id = mailId, MailAccountId = accountId, MailFolderId = childId, Uid = 5 });
            await db.SaveChangesAsync();
        }

        await ExpectProblemAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/parent", new { parentId = childId }), HttpStatusCode.Conflict, "mail_folder_cycle");
        await ExpectProblemAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/parent", new { parentId = custom }), HttpStatusCode.Conflict, "mail_folder_cycle");
        await ExpectProblemAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/parent", new { parentId = otherInbox }), HttpStatusCode.NotFound, "mail_folder_not_found");
        await ExpectProblemAsync(await client.PutAsJsonAsync($"/api/folders/{inbox}/parent", new { parentId = (Guid?)null }), HttpStatusCode.UnprocessableEntity, "mail_folder_protected");
        Assert.DoesNotContain(factory.Remote.Calls, call => call.StartsWith($"move {accountId}", StringComparison.Ordinal));

        factory.Remote.NextOutcome = RemoteFolderOutcome.Rejected;
        var virtualMove = await ReadFolderAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/parent", new { parentId = inbox }), HttpStatusCode.OK);
        Assert.Equal("Eski", virtualMove.GetProperty("fullName").GetString());
        Assert.Equal(inbox, virtualMove.GetProperty("parentId").GetGuid());
        Assert.True(virtualMove.GetProperty("isLocalParentOverride").GetBoolean());
        var original = (await client.GetFromJsonAsync<JsonElement>("/api/folders")).EnumerateArray().ToDictionary(f => f.GetProperty("id").GetGuid());
        Assert.Equal("Eski", original[custom].GetProperty("fullName").GetString());
        Assert.Equal("Eski/Child", original[childId].GetProperty("fullName").GetString());
        Assert.Equal(inbox, original[custom].GetProperty("parentId").GetGuid());

        var moved = await ReadFolderAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/parent", new { parentId = inbox }), HttpStatusCode.OK);
        Assert.Equal("INBOX/Eski", moved.GetProperty("fullName").GetString());
        Assert.Equal(inbox, moved.GetProperty("parentId").GetGuid());
        var nested = (await client.GetFromJsonAsync<JsonElement>("/api/folders")).EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == childId);
        Assert.Equal("INBOX/Eski/Child", nested.GetProperty("fullName").GetString());
        Assert.Equal(custom, nested.GetProperty("parentId").GetGuid());
        Assert.Contains(factory.Remote.Calls, call => call == $"move {accountId} Eski INBOX");
        var rooted = await ReadFolderAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/parent", new { parentId = (Guid?)null }), HttpStatusCode.OK);
        Assert.Equal("Eski", rooted.GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, rooted.GetProperty("parentId").ValueKind);
        using var verify = factory.Services.CreateScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(childId, dbVerify.Mails.Single(mail => mail.Id == mailId).MailFolderId);
        Assert.Equal("Eski/Child", dbVerify.MailFolders.Single(folder => folder.Id == childId).FullName);
    }

    [Fact]
    public async Task Move_CollisionAndUnknownHierarchyLeaveRemoteAndCachedFoldersUntouched()
    {
        var (accountId, inbox, custom) = await SeedAsync();
        var client = Client(accountId);
        var existing = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MailFolders.Add(new Domain.Entities.MailFolder
            {
                Id = existing,
                MailAccountId = accountId,
                Name = "Eski",
                FullName = "INBOX/Eski",
                Delimiter = "/",
                FolderType = MailFolderType.Custom,
                DetectedFolderType = MailFolderType.Custom,
                IsAvailable = true
            });
            await db.SaveChangesAsync();
        }
        var beforeCalls = factory.Remote.Calls.Count;
        await ExpectProblemAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/parent", new { parentId = inbox }), HttpStatusCode.Conflict, "mail_folder_exists");
        Assert.Equal(beforeCalls, factory.Remote.Calls.Count);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("Eski", db.MailFolders.Single(folder => folder.Id == custom).FullName);
            Assert.Equal("INBOX/Eski", db.MailFolders.Single(folder => folder.Id == existing).FullName);
            db.MailFolders.Single(folder => folder.Id == custom).Delimiter = null;
            await db.SaveChangesAsync();
        }
        var virtualRoot = await ReadFolderAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/parent", new { parentId = inbox }), HttpStatusCode.OK);
        Assert.Equal("Eski", virtualRoot.GetProperty("fullName").GetString());
        Assert.Equal(inbox, virtualRoot.GetProperty("parentId").GetGuid());
        Assert.True(virtualRoot.GetProperty("isLocalParentOverride").GetBoolean());
        Assert.Equal(beforeCalls, factory.Remote.Calls.Count);
    }

    [Fact]
    public async Task Delete_RefusesNonemptyServerFolder_AndOnSuccessRemovesCachedMailAndFiles()
    {
        var (accountId, _, custom) = await SeedAsync();
        var client = Client(accountId);
        var mailId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        await factory.Storage.SaveAsync(accountId, mailId, attachmentId, (s, ct) => s.WriteAsync(Encoding.UTF8.GetBytes("x"), ct).AsTask(), 10, CancellationToken.None);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Mails.Add(new Domain.Entities.Mail { Id = mailId, MailAccountId = accountId, MailFolderId = custom, Uid = 1 });
            db.Attachments.Add(new Attachment { Id = attachmentId, MailAccountId = accountId, MailId = mailId, FileName = "a.txt", ContentType = "text/plain", StoragePath = AttachmentPath.Relative(accountId, mailId, attachmentId), SizeBytes = 1 });
            await db.SaveChangesAsync();
        }

        factory.Remote.NextOutcome = RemoteFolderOutcome.NotEmpty;
        await ExpectProblemAsync(await client.DeleteAsync($"/api/folders/{custom}"), HttpStatusCode.Conflict, "mail_folder_not_empty");
        Assert.Contains(factory.Storage.Content.Keys, key => key == AttachmentPath.Relative(accountId, mailId, attachmentId));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/folders/{custom}")).StatusCode);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(verifyDb.MailFolders.Any(f => f.Id == custom));
        Assert.False(verifyDb.Mails.Any(m => m.Id == mailId));
        Assert.DoesNotContain(factory.Storage.Content.Keys, key => key == AttachmentPath.Relative(accountId, mailId, attachmentId));
    }

    [Fact]
    public async Task ComposeLimits_AndRangedAttachmentDownload()
    {
        var (accountId, inbox, _) = await SeedAsync();
        var client = Client(accountId);
        var limits = await client.GetFromJsonAsync<JsonElement>("/api/compose/limits");
        Assert.Equal(25 * 1024 * 1024, limits.GetProperty("maxAttachmentBytes").GetInt64());
        Assert.Equal(50 * 1024 * 1024, limits.GetProperty("maxMessageAttachmentBytes").GetInt64());
        Assert.Equal(20, limits.GetProperty("maxAttachmentCount").GetInt32());

        var mailId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        await factory.Storage.SaveAsync(accountId, mailId, attachmentId, (s, ct) => s.WriteAsync(Encoding.UTF8.GetBytes("0123456789"), ct).AsTask(), 100, CancellationToken.None);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Mails.Add(new Domain.Entities.Mail { Id = mailId, MailAccountId = accountId, MailFolderId = inbox, Uid = 2 });
            db.Attachments.Add(new Attachment { Id = attachmentId, MailAccountId = accountId, MailId = mailId, FileName = "n.bin", ContentType = "application/octet-stream", StoragePath = AttachmentPath.Relative(accountId, mailId, attachmentId), SizeBytes = 10 });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/mails/{mailId}/attachments/{attachmentId}");
        request.Headers.Range = new RangeHeaderValue(4, null);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(4, response.Content.Headers.ContentRange!.From);
        Assert.Equal(10, response.Content.Headers.ContentRange.Length);
        Assert.Equal("456789", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("not a type", "application/octet-stream")]
    [InlineData("multipart/mixed", "application/octet-stream")]
    [InlineData("message/rfc822", "application/octet-stream")]
    public async Task MimeBuilder_KeepsFileNameAndBytes_WithSafeContentType(string declared, string expected)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var message = MimeMessageBuilder.Build("me@example.test", "Me", new MailboxAddress("To", "to@example.test"), "s", null, "b",
            [new SendMailAttachment("rapor.dat", declared, new MemoryStream(bytes))]);
        var part = Assert.Single(message.Attachments.OfType<MimePart>());
        Assert.Equal(expected, part.ContentType.MimeType);
        Assert.Equal("rapor.dat", part.FileName);
        using var decoded = new MemoryStream();
        await part.Content!.DecodeToAsync(decoded);
        Assert.Equal(bytes, decoded.ToArray());
    }

    [Fact]
    public async Task SetFolderRole_OverridesDetectedRole_MovesBetweenFolders_ResetsAndStaysInsideTheAccount()
    {
        var (accountId, inbox, custom) = await SeedAsync();
        var (otherAccountId, _, otherCustom) = await SeedAsync();
        var detectedSent = Guid.NewGuid();
        var sentItems = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MailFolders.Add(new Domain.Entities.MailFolder { Id = detectedSent, MailAccountId = accountId, Name = "Sent", FullName = "INBOX.Sent", Delimiter = ".", FolderType = MailFolderType.Sent, DetectedFolderType = MailFolderType.Sent, IsAvailable = true });
            db.MailFolders.Add(new Domain.Entities.MailFolder { Id = sentItems, MailAccountId = accountId, Name = "Sent Items", FullName = "INBOX.Sent Items", Delimiter = ".", FolderType = MailFolderType.Custom, DetectedFolderType = MailFolderType.Custom, IsAvailable = true });
            await db.SaveChangesAsync();
        }
        var client = Client(accountId);

        var assigned = await ReadFolderAsync(await client.PutAsJsonAsync($"/api/folders/{sentItems}/role", new { role = "Sent" }), HttpStatusCode.OK);
        Assert.Equal("Sent", assigned.GetProperty("folderType").GetString());
        Assert.Equal("Sent", assigned.GetProperty("folderRoleOverride").GetString());
        var folders = (await client.GetFromJsonAsync<JsonElement>("/api/folders")).EnumerateArray().ToDictionary(f => f.GetProperty("id").GetGuid());
        Assert.Equal("Custom", folders[detectedSent].GetProperty("folderType").GetString());
        Assert.Equal("Inbox", folders[inbox].GetProperty("folderType").GetString());

        var moved = await ReadFolderAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/role", new { role = "Sent" }), HttpStatusCode.OK);
        Assert.Equal("Sent", moved.GetProperty("folderType").GetString());
        folders = (await client.GetFromJsonAsync<JsonElement>("/api/folders")).EnumerateArray().ToDictionary(f => f.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, folders[sentItems].GetProperty("folderRoleOverride").ValueKind);
        Assert.Equal("Custom", folders[sentItems].GetProperty("folderType").GetString());

        var reset = await ReadFolderAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/role", new { role = (string?)null }), HttpStatusCode.OK);
        Assert.Equal("Custom", reset.GetProperty("folderType").GetString());
        folders = (await client.GetFromJsonAsync<JsonElement>("/api/folders")).EnumerateArray().ToDictionary(f => f.GetProperty("id").GetGuid());
        Assert.Equal("Sent", folders[detectedSent].GetProperty("folderType").GetString());

        await ExpectProblemAsync(await client.PutAsJsonAsync($"/api/folders/{custom}/role", new { role = "Inbox" }), HttpStatusCode.BadRequest, "invalid_folder_role");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/folders/{otherCustom}/role", new { role = "Trash" })).StatusCode);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(verifyDb.MailFolders.Single(f => f.Id == otherCustom).FolderRoleOverride);
        Assert.All(verifyDb.MailFolders.Where(f => f.MailAccountId == otherAccountId).ToList(), f => Assert.Null(f.FolderRoleOverride));
    }

    [Theory]
    [InlineData("Sent Items", MailFolderType.Sent)]
    [InlineData("Drafts", MailFolderType.Drafts)]
    [InlineData("Deleted Items", MailFolderType.Trash)]
    [InlineData("Spam", MailFolderType.Junk)]
    [InlineData("Archive", MailFolderType.Archive)]
    [InlineData("INBOX/Projects", MailFolderType.Custom)]
    public void Classify_UsesTopLevelNamesWithoutSpecialUse(string name, MailFolderType expected)
    {
        Assert.Equal(expected, MailClient.Infrastructure.Services.MailFolderDiscovery.Classify(MailKit.FolderAttributes.None, name));
    }

    [Fact]
    public void ApplyRoles_KeepsOverrideWhenRediscoveryReportsAnotherSentFolder()
    {
        var sent = new Domain.Entities.MailFolder { FullName = "INBOX.Sent", DetectedFolderType = MailFolderType.Sent };
        var sentItems = new Domain.Entities.MailFolder { FullName = "INBOX.Sent Items", DetectedFolderType = MailFolderType.Custom, FolderRoleOverride = MailFolderType.Sent };
        var trash = new Domain.Entities.MailFolder { FullName = "INBOX.Trash", DetectedFolderType = MailFolderType.Trash };

        Domain.Entities.MailFolder.ApplyRoles([sent, sentItems, trash]);

        Assert.Equal(MailFolderType.Sent, sentItems.FolderType);
        Assert.Equal(MailFolderType.Sent, sentItems.FolderRoleOverride);
        Assert.Equal(MailFolderType.Custom, sent.FolderType);
        Assert.Equal(MailFolderType.Trash, trash.FolderType);
    }

    private HttpClient Client(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private async Task<(Guid AccountId, Guid Inbox, Guid Custom)> SeedAsync()
    {
        var accountId = Guid.NewGuid();
        var inbox = Guid.NewGuid();
        var custom = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@mail.test.invalid",
            NormalizedEmailAddress = $"{accountId:N}@MAIL.TEST.INVALID",
            Username = accountId.ToString("N"),
            ImapHost = "mail.test.invalid",
            ImapPort = 993,
            SmtpHost = "mail.test.invalid",
            SmtpPort = 465,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new Domain.Entities.MailFolder { Id = inbox, MailAccountId = accountId, Name = "INBOX", FullName = "INBOX", Delimiter = "/", FolderType = MailFolderType.Inbox, DetectedFolderType = MailFolderType.Inbox, IsAvailable = true });
        db.MailFolders.Add(new Domain.Entities.MailFolder { Id = custom, MailAccountId = accountId, Name = "Eski", FullName = "Eski", Delimiter = "/", FolderType = MailFolderType.Custom, DetectedFolderType = MailFolderType.Custom, IsAvailable = true });
        await db.SaveChangesAsync();
        return (accountId, inbox, custom);
    }

    private static async Task<JsonElement> ReadFolderAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
