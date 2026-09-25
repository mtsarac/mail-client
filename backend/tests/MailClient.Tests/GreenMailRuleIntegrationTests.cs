using System.Text.Json;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using MailFolder = MailClient.Domain.Entities.MailFolder;

namespace MailClient.Tests;

[Collection("greenmail")]
public sealed class GreenMailRuleIntegrationTests(GreenMailFixture greenmail) : IAsyncLifetime
{
    private readonly string _databaseName = $"mailclient_rules_{Guid.NewGuid():N}";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        if (!IntegrationEnvironment.GreenMailEnabled || !IntegrationEnvironment.PostgresEnabled)
            return;
        await using (var admin = new NpgsqlConnection(IntegrationEnvironment.PostgresAdmin))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }
        _connectionString = new NpgsqlConnectionStringBuilder(IntegrationEnvironment.PostgresAdmin) { Database = _databaseName }.ConnectionString;
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_connectionString)) return;
        await using var admin = new NpgsqlConnection(IntegrationEnvironment.PostgresAdmin);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE \"{_databaseName}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task NewMail_RunsServerRuleOnRealImap_AndExistingMailIsLeftAlone()
    {
        if (!IntegrationEnvironment.GreenMailEnabled || !IntegrationEnvironment.PostgresEnabled) return;
        var token = $"rule-{Guid.NewGuid():N}";
        var targetName = $"Rules-{token}";
        using (var setup = await ConnectAsync())
            await (await setup.GetFolderAsync(setup.PersonalNamespaces[0].Path)).CreateAsync(targetName, true);
        await AppendAsync($"existing-{token}");

        await using var db = CreateDb();
        var accountId = Guid.NewGuid();
        var inboxId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@localhost",
            NormalizedEmailAddress = $"{accountId:N}@LOCALHOST",
            Username = greenmail.Username,
            ImapHost = greenmail.Host,
            ImapPort = greenmail.ImapPort,
            SmtpHost = greenmail.Host,
            SmtpPort = 3025,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = inboxId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsAvailable = true,
            IsSyncEnabled = true
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = targetId,
            MailAccountId = accountId,
            Name = targetName,
            FullName = targetName,
            FolderType = MailFolderType.Custom,
            IsAvailable = true
        });
        db.MailRules.Add(new MailRule
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Name = "rule",
            Enabled = true,
            Logic = "And",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ConditionJson = JsonSerializer.Serialize(new[] { new RuleCondition("subjectContains", token) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ActionJson = JsonSerializer.Serialize(new[] { new RuleAction("markRead"), new RuleAction("move", FolderId: targetId) }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });
        await db.SaveChangesAsync();

        var sync = CreateSyncService(db);
        await SyncInboxAsync(sync, accountId, inboxId);
        await AppendAsync($"new-{token}");
        var unfiltered = $"unfiltered-{Guid.NewGuid():N}";
        await AppendAsync(unfiltered);
        await SyncInboxAsync(sync, accountId, inboxId);
        Assert.True(await db.Mails.AnyAsync(mail => mail.Subject == $"new-{token}" && mail.RulePending));
        Assert.False(await db.Mails.AnyAsync(mail => mail.Subject == $"existing-{token}" && mail.RulePending));

        var folders = new GreenMailFolderClient(greenmail);
        var audit = new AuditLogger(db);
        var operations = new MailOperationService(db, folders, new MailReadService(db, folders, audit, NullLogger<MailReadService>.Instance),
            audit, new FakeSyncScheduler(), NullLogger<MailOperationService>.Instance, new FakePushNotificationService(), new FakeFileStorage());
        await new MailRuleEvaluator(db, operations, NullLogger<MailRuleEvaluator>.Instance)
            .EvaluatePendingAsync(accountId, inboxId, CancellationToken.None);

        using var imap = await ConnectAsync();
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.Single(await imap.Inbox.SearchAsync(SearchQuery.SubjectContains($"existing-{token}")));
        Assert.Empty(await imap.Inbox.SearchAsync(SearchQuery.SubjectContains($"new-{token}")));
        var target = await imap.GetFolderAsync(targetName);
        await target.OpenAsync(FolderAccess.ReadOnly);
        var moved = Assert.Single(await target.SearchAsync(SearchQuery.SubjectContains($"new-{token}")));
        var summary = Assert.Single(await target.FetchAsync([moved], MessageSummaryItems.Flags));
        Assert.True(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
        Assert.False(await db.Mails.AsNoTracking().AnyAsync(mail => mail.MailAccountId == accountId && mail.RulePending));

        var push = new FakePushNotificationService();
        await new NewMailNotifier(db, push, NullLogger<NewMailNotifier>.Instance).NotifyPendingAsync(accountId, CancellationToken.None);
        var notification = Assert.Single(push.Notifications);
        Assert.Equal(unfiltered, notification.SubjectPreview);
        Assert.False(await db.Mails.AsNoTracking().AnyAsync(mail => mail.MailAccountId == accountId && mail.NotificationPending));
    }

    private async Task SyncInboxAsync(MailFolderSyncService sync, Guid accountId, Guid inboxId)
    {
        using var imap = await ConnectAsync();
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
        await sync.SyncFolderCoreAsync(accountId, inboxId, new MailKitRemoteMailFolder(imap.Inbox), CancellationToken.None);
    }

    private async Task AppendAsync(string subject)
    {
        using var imap = await ConnectAsync();
        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MimeKit.MailboxAddress("GreenMail", greenmail.Username));
        message.Subject = subject;
        message.Body = new MimeKit.TextPart("plain") { Text = subject };
        await imap.Inbox.AppendAsync(message, MessageFlags.None);
        await imap.DisconnectAsync(true);
    }

    private async Task<ImapClient> ConnectAsync()
    {
        var imap = new ImapClient();
        await imap.ConnectAsync(greenmail.Host, greenmail.ImapPort, SecureSocketOptions.None);
        await imap.AuthenticateAsync(greenmail.Username, greenmail.Password);
        return imap;
    }

    private AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private static MailFolderSyncService CreateSyncService(AppDbContext db) => new(db, TestServices.Credentials(db),
        new MailConnectionHelper(new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)), NullLogger<MailConnectionHelper>.Instance),
        new FakeFileStorage(), FixedRuntimeSettingsStore.Operation(TestServices.SyncSettings()), new FakePushNotificationService(),
        new ConversationService(db), new MailReconciliationService(db), NullLogger<MailFolderSyncService>.Instance);

    private sealed class GreenMailFolderClient(GreenMailFixture greenmail) : IMailFolderClient
    {
        public async Task<T> UseFolderAsync<T>(MailAccount account, string fullName, bool forUpdate,
            Func<IRemoteMailFolder, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(greenmail.Host, greenmail.ImapPort, SecureSocketOptions.None, cancellationToken);
            await imap.AuthenticateAsync(greenmail.Username, greenmail.Password, cancellationToken);
            var remote = new MailKitRemoteMailFolder(await imap.GetFolderAsync(fullName, cancellationToken), imap.GetFolderAsync);
            if (forUpdate)
                await remote.OpenForUpdateAsync(cancellationToken);
            else
                await remote.OpenAsync(cancellationToken);
            var result = await action(remote, cancellationToken);
            await imap.DisconnectAsync(true, cancellationToken);
            return result;
        }
    }
}
