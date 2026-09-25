using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Api.Endpoints;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class SignatureIdentityApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Signatures_CreateListUpdateDelete_RoundTrips_WithDefaults()
    {
        var client = await ClientForNewAccountAsync();

        var created = await client.PostAsJsonAsync("/api/signatures", new SignatureRequest("Work", "Best regards", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var signature = (await created.Content.ReadFromJsonAsync<SignatureResponse>())!;
        Assert.Equal("Work", signature.Name);

        var other = (await (await client.PostAsJsonAsync("/api/signatures", new SignatureRequest("Personal", "Cheers", "<p>Cheers</p>")))
            .Content.ReadFromJsonAsync<SignatureResponse>())!;

        var list = await client.GetFromJsonAsync<SignatureListResponse>("/api/signatures");
        Assert.Equal(2, list!.Items.Count);
        Assert.Null(list.Defaults.NewMailSignatureId);

        var defaults = await client.PutAsJsonAsync("/api/signatures/defaults",
            new SignatureDefaultsRequest(signature.Id, other.Id, null));
        Assert.Equal(HttpStatusCode.OK, defaults.StatusCode);
        var updated = (await defaults.Content.ReadFromJsonAsync<SignatureDefaultsResponse>())!;
        Assert.Equal(signature.Id, updated.NewMailSignatureId);
        Assert.Equal(other.Id, updated.ReplySignatureId);
        Assert.Null(updated.ForwardSignatureId);

        var renamed = await client.PutAsJsonAsync($"/api/signatures/{signature.Id}",
            new SignatureRequest("Business", "Kind regards", null));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Business", (await renamed.Content.ReadFromJsonAsync<SignatureResponse>())!.Name);

        var deleted = await client.DeleteAsync($"/api/signatures/{signature.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var afterDelete = await client.GetFromJsonAsync<SignatureListResponse>("/api/signatures");
        Assert.Null(afterDelete!.Defaults.NewMailSignatureId);
        Assert.Single(afterDelete.Items);
    }

    [Fact]
    public async Task Signatures_ValidationAndForeignIds_Fail()
    {
        var client = await ClientForNewAccountAsync();
        var otherClient = await ClientForNewAccountAsync();

        var blank = await client.PostAsJsonAsync("/api/signatures", new SignatureRequest(" ", "body", null));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var foreign = (await (await otherClient.PostAsJsonAsync("/api/signatures", new SignatureRequest("Other", "body", null)))
            .Content.ReadFromJsonAsync<SignatureResponse>())!;
        var badDefaults = await client.PutAsJsonAsync("/api/signatures/defaults",
            new SignatureDefaultsRequest(foreign.Id, null, null));
        Assert.Equal(HttpStatusCode.NotFound, badDefaults.StatusCode);
        Assert.Equal("signature_not_found", await CodeAsync(badDefaults));

        var missing = await client.PutAsJsonAsync($"/api/signatures/{Guid.NewGuid()}",
            new SignatureRequest("Name", "body", null));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("signature_not_found", await CodeAsync(missing));
    }

    [Fact]
    public async Task Signatures_AreScopedPerAccount()
    {
        var client = await ClientForNewAccountAsync();
        var otherClient = await ClientForNewAccountAsync();
        var foreign = (await (await client.PostAsJsonAsync("/api/signatures", new SignatureRequest("Mine", "body", null)))
            .Content.ReadFromJsonAsync<SignatureResponse>())!;

        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.PutAsJsonAsync($"/api/signatures/{foreign.Id}", new SignatureRequest("Theirs", "body", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync($"/api/signatures/{foreign.Id}")).StatusCode);
        Assert.Empty((await otherClient.GetFromJsonAsync<SignatureListResponse>("/api/signatures"))!.Items);
    }

    [Fact]
    public async Task LegacySignature_CompatRoundTrips_ThroughDefaults()
    {
        var client = await ClientForNewAccountAsync();

        var set = await client.PutAsJsonAsync("/api/account/signature", new AccountSignatureRequest("Saygılar"));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        var account = (await client.GetFromJsonAsync<JsonDocument>("/api/account"))!.RootElement;
        Assert.Equal("Saygılar", account.GetProperty("signature").GetString());

        var list = await client.GetFromJsonAsync<SignatureListResponse>("/api/signatures");
        Assert.Single(list!.Items);
        Assert.Equal(list.Items[0].Id, list.Defaults.NewMailSignatureId);

        var clear = await client.PutAsJsonAsync("/api/account/signature", new AccountSignatureRequest("   "));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        var afterClear = (await client.GetFromJsonAsync<JsonDocument>("/api/account"))!.RootElement;
        Assert.Equal(JsonValueKind.Null, afterClear.GetProperty("signature").ValueKind);
    }

    [Fact]
    public async Task Identities_CreateListUpdateDelete_RoundTrips()
    {
        var client = await ClientForNewAccountAsync();
        var signature = (await (await client.PostAsJsonAsync("/api/signatures", new SignatureRequest("Work", "body", null)))
            .Content.ReadFromJsonAsync<SignatureResponse>())!;

        var created = await client.PostAsJsonAsync("/api/identities",
            new IdentityRequest("alias@example.test", "Alias", null, signature.Id, IsDefault: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var identity = (await created.Content.ReadFromJsonAsync<IdentityResponse>())!;
        Assert.Equal("alias@example.test", identity.EmailAddress);
        Assert.True(identity.IsDefault);

        var second = (await (await client.PostAsJsonAsync("/api/identities",
                new IdentityRequest("second@example.test", "", null, null, IsDefault: true)))
            .Content.ReadFromJsonAsync<IdentityResponse>())!;
        var list = await client.GetFromJsonAsync<IdentityListResponse>("/api/identities");
        Assert.Equal(2, list!.Items.Count);
        Assert.Single(list.Items.Where(x => x.IsDefault), x => x.Id == second.Id);

        var updated = await client.PutAsJsonAsync($"/api/identities/{identity.Id}",
            new IdentityRequest("alias@example.test", "Alias Updated", "reply@example.test", null, IsDefault: false));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Alias Updated", (await updated.Content.ReadFromJsonAsync<IdentityResponse>())!.DisplayName);

        var deleted = await client.DeleteAsync($"/api/identities/{second.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<IdentityListResponse>("/api/identities"))!.Items);
    }

    [Fact]
    public async Task Identities_DuplicateAndValidation_Fail()
    {
        var client = await ClientForNewAccountAsync();
        var created = await client.PostAsJsonAsync("/api/identities",
            new IdentityRequest("alias@example.test", "", null, null, IsDefault: false));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/identities",
            new IdentityRequest("ALIAS@example.test", "", null, null, IsDefault: false));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("identity_already_exists", await CodeAsync(duplicate));

        var invalid = await client.PostAsJsonAsync("/api/identities",
            new IdentityRequest("not-an-email", "", null, null, IsDefault: false));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var badSignature = await client.PostAsJsonAsync("/api/identities",
            new IdentityRequest("other@example.test", "", null, Guid.NewGuid(), IsDefault: false));
        Assert.Equal(HttpStatusCode.NotFound, badSignature.StatusCode);
        Assert.Equal("signature_not_found", await CodeAsync(badSignature));
    }

    [Fact]
    public async Task Identities_AreScopedPerAccount()
    {
        var client = await ClientForNewAccountAsync();
        var otherClient = await ClientForNewAccountAsync();
        var identity = (await (await client.PostAsJsonAsync("/api/identities",
                new IdentityRequest("alias@example.test", "", null, null, IsDefault: false)))
            .Content.ReadFromJsonAsync<IdentityResponse>())!;

        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.DeleteAsync($"/api/identities/{identity.Id}")).StatusCode);
        Assert.Empty((await otherClient.GetFromJsonAsync<IdentityListResponse>("/api/identities"))!.Items);
    }

    [Fact]
    public async Task Identity_InUseByPendingScheduledSend_BlocksDelete()
    {
        var accountId = await SeedAccountAsync();
        var client = ClientFor(accountId);
        var identity = (await (await client.PostAsJsonAsync("/api/identities",
                new IdentityRequest("alias@example.test", "", null, null, IsDefault: false)))
            .Content.ReadFromJsonAsync<IdentityResponse>())!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var service = new ScheduledSendService(db, new FakeFileStorage(),
                FixedRuntimeSettingsStore.Operation(), new AuditLogger(db), NullLogger<ScheduledSendService>.Instance);
            var command = new SendMailCommand(accountId, ["friend@example.test"], [], [], "Hello", null, "body", [])
            {
                IdempotencyKey = "identity-guard",
                IdentityId = identity.Id
            };
            await service.CreateAsync(accountId, command, DateTime.UtcNow.AddHours(1), null, CancellationToken.None);
        }

        var blocked = await client.DeleteAsync($"/api/identities/{identity.Id}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("identity_in_use", await CodeAsync(blocked));
    }

    [Fact]
    public async Task Send_WithIdentity_UsesIdentityFromAndReplyTo()
    {
        await using var db = CreateDb();
        var accountId = await SeedServiceAccountAsync(db);
        var identity = new MailIdentity
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            EmailAddress = "alias@example.test",
            DisplayName = "Alias",
            ReplyTo = "reply@example.test"
        };
        db.MailIdentities.Add(identity);
        await db.SaveChangesAsync();
        var transport = new FakeMailTransport();
        var service = CreateSendService(db, transport);
        var command = new SendMailCommand(accountId, ["friend@example.test"], [], [], "Hello", null, "body", [])
        {
            IdempotencyKey = "identity-send",
            IdentityId = identity.Id
        };

        await service.SendAsync(accountId, command, null, CancellationToken.None);

        Assert.Equal("alias@example.test", transport.Message!.From.Mailboxes.Single().Address);
        Assert.Equal("reply@example.test", transport.Message.ReplyTo.Mailboxes.Single().Address);
    }

    [Fact]
    public async Task Send_WithUnknownIdentity_ThrowsIdentityNotFound()
    {
        await using var db = CreateDb();
        var accountId = await SeedServiceAccountAsync(db);
        var otherAccountId = await SeedServiceAccountAsync(db);
        var foreign = new MailIdentity
        {
            Id = Guid.NewGuid(),
            MailAccountId = otherAccountId,
            EmailAddress = "foreign@example.test",
            DisplayName = ""
        };
        db.MailIdentities.Add(foreign);
        await db.SaveChangesAsync();
        var service = CreateSendService(db, new FakeMailTransport());

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(accountId,
            new SendMailCommand(accountId, ["friend@example.test"], [], [], "Hello", null, "body", [])
            {
                IdempotencyKey = "identity-unknown",
                IdentityId = Guid.NewGuid()
            }, null, CancellationToken.None));
        var crossAccount = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(accountId,
            new SendMailCommand(accountId, ["friend@example.test"], [], [], "Hello", null, "body", [])
            {
                IdempotencyKey = "identity-foreign",
                IdentityId = foreign.Id
            }, null, CancellationToken.None));

        Assert.Equal("identity_not_found", unknown.Message);
        Assert.Equal("identity_not_found", crossAccount.Message);
    }

    [Fact]
    public async Task ScheduledCreate_WithUnknownIdentity_ThrowsIdentityNotFound()
    {
        await using var db = CreateDb();
        var accountId = await SeedServiceAccountAsync(db);
        var service = new ScheduledSendService(db, new FakeFileStorage(),
            FixedRuntimeSettingsStore.Operation(), new AuditLogger(db), NullLogger<ScheduledSendService>.Instance);
        var command = new SendMailCommand(accountId, ["friend@example.test"], [], [], "Hello", null, "body", [])
        {
            IdempotencyKey = "identity-sched",
            IdentityId = Guid.NewGuid()
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(accountId, command, DateTime.UtcNow.AddHours(1), null, CancellationToken.None));

        Assert.Equal("identity_not_found", error.Message);
    }

    [Fact]
    public async Task DraftSend_WithUnknownFromAddress_ThrowsIdentityNotFound()
    {
        await using var db = CreateDb();
        var accountId = await SeedServiceAccountAsync(db, drafts: true);
        var folder = await db.MailFolders.SingleAsync(x => x.MailAccountId == accountId);
        var draftId = Guid.NewGuid();
        db.Mails.Add(new Domain.Entities.Mail
        {
            Id = draftId,
            MailAccountId = accountId,
            MailFolderId = folder.Id,
            Uid = 5,
            UidValidity = 31,
            Draft = true,
            MessageId = "draft@example.test",
            Subject = "old",
            FromAddress = "ghost@example.test",
            BodyText = "body"
        });
        db.Participants.Add(new MailParticipant
        {
            Id = Guid.NewGuid(),
            MailId = draftId,
            Type = ParticipantType.To,
            Address = "to@example.test",
            NormalizedAddress = "to@example.test"
        });
        await db.SaveChangesAsync();
        var folders = new RecordingDraftFolderClient(new FakeRemoteMailFolder(31, new()));
        var audit = new AuditLogger(db);
        var reader = new MailReadService(db, folders, audit, NullLogger<MailReadService>.Instance);
        var operations = new MailOperationService(db, folders, reader, audit, new FakeSyncScheduler(),
            NullLogger<MailOperationService>.Instance, new FakePushNotificationService(), new FakeFileStorage());
        var sendOperations = new SendOperationStore(db, NullLogger<SendOperationStore>.Instance);
        var sender = new MailSendService(db, new FakeMailTransport(), sendOperations,
            FixedRuntimeSettingsStore.Operation(), TestServices.InlineSync(), audit, NullLogger<MailSendService>.Instance);
        var service = new DraftService(db, folders, TestServices.InlineSync(), reader, operations, sender,
            sendOperations, new FakeFileStorage(), audit, NullLogger<DraftService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendAsync(accountId, draftId, "draft-identity", null, CancellationToken.None));

        Assert.Equal("identity_not_found", error.Message);
    }

    [Fact]
    public void PersistenceModel_SignatureAndIdentityRelations_AreConfigured()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

        var signature = db.Model.FindEntityType(typeof(MailSignature))!;
        Assert.Equal(DeleteBehavior.Cascade,
            signature.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(MailAccount)).DeleteBehavior);
        var identity = db.Model.FindEntityType(typeof(MailIdentity))!;
        Assert.Equal(DeleteBehavior.Cascade,
            identity.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(MailAccount)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.SetNull,
            identity.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(MailSignature)).DeleteBehavior);
        var scheduled = db.Model.FindEntityType(typeof(ScheduledSend))!;
        Assert.Equal(DeleteBehavior.SetNull,
            scheduled.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(MailIdentity)).DeleteBehavior);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("code").GetString();

    private HttpClient ClientFor(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private async Task<HttpClient> ClientForNewAccountAsync() => ClientFor(await SeedAccountAsync());

    private async Task<Guid> SeedAccountAsync()
    {
        var accountId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@example.test",
            NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
            Username = "signature-test",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        await db.SaveChangesAsync();
        return accountId;
    }

    private static MailSendService CreateSendService(AppDbContext db, FakeMailTransport transport) =>
        new(db, transport, new SendOperationStore(db, NullLogger<SendOperationStore>.Instance),
            FixedRuntimeSettingsStore.Operation(), TestServices.InlineSync(), new AuditLogger(db),
            NullLogger<MailSendService>.Instance);

    private static async Task<Guid> SeedServiceAccountAsync(AppDbContext db, bool drafts = false)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "me@example.test",
            NormalizedEmailAddress = "ME@EXAMPLE.TEST",
            Username = "me",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active,
            SaveSentCopy = false
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = drafts ? "Drafts" : "Sent",
            FullName = drafts ? "Drafts" : "Sent",
            FolderType = drafts ? MailFolderType.Drafts : MailFolderType.Sent,
            IsAvailable = true,
            IsSyncEnabled = true,
            UidValidity = 31
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return accountId;
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class RecordingDraftFolderClient(FakeRemoteMailFolder remote) : IMailFolderClient
    {
        public Task<T> UseFolderAsync<T>(MailAccount account, string fullName, bool forUpdate,
            Func<IRemoteMailFolder, CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
            action(remote, cancellationToken);
    }
}
