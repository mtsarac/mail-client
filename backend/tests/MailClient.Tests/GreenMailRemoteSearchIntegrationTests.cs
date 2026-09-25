using MailFolder = MailClient.Domain.Entities.MailFolder;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using MailClient.Infrastructure.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MailClient.Tests;

[Collection("greenmail")]
public sealed class GreenMailRemoteSearchIntegrationTests(GreenMailFixture greenmail) : IAsyncLifetime
{
    private readonly string _databaseName = $"mailclient_remote_search_{Guid.NewGuid():N}";
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
    public async Task RemoteSearch_ImportsOlderRealImapMatch_AndSearchFindsIt()
    {
        if (!IntegrationEnvironment.GreenMailEnabled || !IntegrationEnvironment.PostgresEnabled) return;
        var token = $"remote-search-{Guid.NewGuid():N}";
        var ccAddress = $"cc-{Guid.NewGuid():N}@example.test";
        await SeedAsync($"older-{token}", token, ccAddress);
        await SeedAsync($"middle-{token}", token);
        await SeedAsync($"newest-{token}", token);

        await using var db = CreateDb();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
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
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsAvailable = true,
            IsSyncEnabled = true
        });
        await db.SaveChangesAsync();

        var runtime = FixedRuntimeSettingsStore.Operation(TestServices.SyncSettings(maxMessagesPerRun: 2,
            maxAttachmentBytes: 1024 * 1024, maxMessageAttachmentBytes: 10 * 1024 * 1024));
        var sync = CreateSyncService(db, runtime);
        using var imap = await ConnectAsync();
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, CancellationToken.None);
        var remote = new MailKitRemoteMailFolder(inbox);
        await sync.SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        var initial = await db.Mails.Where(mail => mail.MailAccountId == accountId).Select(mail => mail.Subject).ToListAsync();
        Assert.DoesNotContain(initial, subject => subject == $"older-{token}");

        var remoteSearch = new MailRemoteSearchService(db, TestServices.Credentials(db),
            new MailConnectionHelper(new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)), NullLogger<MailConnectionHelper>.Instance),
            sync, runtime, new MailClient.Infrastructure.Sync.InMemorySyncLockProvider(), TimeSpan.FromSeconds(5));
        var progress = new MailRemoteSearchService.SearchProgress();
        var folders = await db.MailFolders.AsNoTracking().Where(folder => folder.Id == folderId).ToListAsync();
        await remoteSearch.SearchAndImportCoreAsync(accountId, folders,
            MailRemoteSearchService.BuildQuery(new MailSearchRequest(token, folderId, null, null, ccAddress, null, null, null, null, null, 1, 0), token), progress,
            (_, _) => Task.FromResult<IRemoteMailFolder>(remote), CancellationToken.None);

        Assert.Equal(1, progress.Matched);
        Assert.Equal(1, progress.Imported);
        Assert.True(progress.Complete);
        var search = new MailSearchService(db, runtime);
        var indexed = await search.SearchAsync(accountId,
            new MailSearchRequest(token, folderId, null, null, ccAddress, null, null, null, null, null, 1, 0), CancellationToken.None);
        Assert.Contains(indexed.Items, item => item.Subject == $"older-{token}");

        var second = new MailRemoteSearchService.SearchProgress();
        await remoteSearch.SearchAndImportCoreAsync(accountId, folders,
            MailRemoteSearchService.BuildQuery(new MailSearchRequest(token, folderId, null, null, ccAddress, null, null, null, null, null, 1, 0), token), second,
            (_, _) => Task.FromResult<IRemoteMailFolder>(remote), CancellationToken.None);
        Assert.Equal(0, second.Matched);
        Assert.Equal(0, second.Imported);
    }

    private async Task SeedAsync(string subject, string body, string? ccAddress = null)
    {
        using var imap = await ConnectAsync();
        var remote = new MailKitRemoteMailFolder(imap.Inbox);
        await remote.OpenForUpdateAsync(CancellationToken.None);
        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MimeKit.MailboxAddress("GreenMail", greenmail.Username));
        if (ccAddress is not null)
            message.Cc.Add(new MimeKit.MailboxAddress("CC", ccAddress));
        message.Subject = subject;
        message.Body = new MimeKit.TextPart("plain") { Text = body };
        await remote.AppendAsync(message, MessageFlags.None, CancellationToken.None);
        await imap.DisconnectAsync(true, CancellationToken.None);
    }

    private async Task<ImapClient> ConnectAsync()
    {
        var imap = new ImapClient();
        await imap.ConnectAsync(greenmail.Host, greenmail.ImapPort, SecureSocketOptions.None, CancellationToken.None);
        await imap.AuthenticateAsync(greenmail.Username, greenmail.Password, CancellationToken.None);
        return imap;
    }

    private AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private static MailFolderSyncService CreateSyncService(AppDbContext db, RuntimeOperationSettings runtime) => new(
        db,
        TestServices.Credentials(db),
        new MailConnectionHelper(new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)), NullLogger<MailConnectionHelper>.Instance),
        new FakeFileStorage(),
        runtime,
        new FakePushNotificationService(),
        new ConversationService(db), new MailReconciliationService(db), NullLogger<MailFolderSyncService>.Instance);
}
