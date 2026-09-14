using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace MailClient.Tests;

[CollectionDefinition("greenmail")]
public sealed class GreenMailCollection : ICollectionFixture<GreenMailFixture>;

/// <summary>
/// GreenMail is a throwaway local test server. Its published ports are plaintext only
/// (no TLS listener is exposed), so the fixture drives the real IMAP/SMTP protocol with
/// raw MailKit clients and then exercises the production sync core against it.
/// </summary>
public sealed class GreenMailFixture : IAsyncLifetime
{
    public string Host => IntegrationEnvironment.GreenMailHost;
    public int SmtpPort => IntegrationEnvironment.GreenMailSmtpPort;
    public int ImapPort => IntegrationEnvironment.GreenMailImapPort;
    public string Username => IntegrationEnvironment.GreenMailUsername;
    public string Password => IntegrationEnvironment.GreenMailPassword;

    public async Task InitializeAsync()
    {
        if (!IntegrationEnvironment.GreenMailEnabled) return;
        if (!await ProbeAsync(SmtpPort) || !await ProbeAsync(ImapPort))
            throw new InvalidOperationException(
                $"GreenMail is not reachable at {Host}:{SmtpPort}/{ImapPort}. Start it or set MAILCLIENT_SKIP_INTEGRATION=1.");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<bool> ProbeAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(Host, port).WaitAsync(TimeSpan.FromSeconds(5));
            return client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

[Collection("greenmail")]
public sealed class GreenMailIntegrationTests(GreenMailFixture greenmail)
{
    [Fact]
    public async Task RealImap_SyncCore_ImportsMessagesWithFlagsAndUidValidity()
    {
        if (!IntegrationEnvironment.GreenMailEnabled) return;
        var unread = $"unread-{Guid.NewGuid():N}";
        var seen = $"seen-{Guid.NewGuid():N}";
        await DeliverAsync(unread, markSeen: false);
        await DeliverAsync(seen, markSeen: true);

        using var imap = await ConnectAsync();
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, CancellationToken.None);
        var remote = new MailKitRemoteMailFolder(inbox);

        await using var db = NewDb();
        var (accountId, folderId) = await SeedAsync(db);
        await CreateSyncService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);

        var mails = await db.Mails.ToListAsync();
        var importedUnread = Assert.Single(mails, mail => mail.Subject == unread);
        var importedSeen = Assert.Single(mails, mail => mail.Subject == seen);
        Assert.False(importedUnread.IsRead);
        Assert.True(importedSeen.IsRead);
        Assert.All(mails, mail => Assert.Equal(remote.UidValidity, mail.UidValidity));
    }

    [Fact]
    public async Task RealImap_ProductionRemoteFolder_AppendsSentCopy()
    {
        if (!IntegrationEnvironment.GreenMailEnabled) return;
        var subject = $"appended-{Guid.NewGuid():N}";
        using var imap = await ConnectAsync();
        var inbox = await imap.GetFolderAsync("INBOX", CancellationToken.None);
        var remote = new MailKitRemoteMailFolder(inbox);
        await remote.OpenForUpdateAsync(CancellationToken.None);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("GreenMail", greenmail.Username));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "appended body" };
        await remote.AppendAsync(message, CancellationToken.None);

        var uids = await inbox.SearchAsync(SearchQuery.SubjectContains(subject), CancellationToken.None);
        Assert.Single(uids);
    }

    [Fact]
    public async Task ProductionHelper_BlocksGreenMailLoopbackEndpoint()
    {
        if (!IntegrationEnvironment.GreenMailEnabled) return;
        var helper = new MailConnectionHelper(
            new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Parse("93.184.216.34"))),
            NullLogger<MailConnectionHelper>.Instance);
        var endpoint = new MailServerEndpoint(greenmail.Host, greenmail.ImapPort, MailSecurity.SslOnConnect);

        var failure = await Assert.ThrowsAsync<MailConnectionException>(() =>
            helper.WithImapAsync(endpoint, greenmail.Username, greenmail.Password, "ValidateImap", static (_, _) => Task.FromResult(true), CancellationToken.None));

        Assert.DoesNotContain("127.0.0.1", failure.Message);
        Assert.DoesNotContain(greenmail.Password, failure.Message);
    }

    private async Task<ImapClient> ConnectAsync()
    {
        var imap = new ImapClient();
        await imap.ConnectAsync(greenmail.Host, greenmail.ImapPort, SecureSocketOptions.None, CancellationToken.None);
        await imap.AuthenticateAsync(greenmail.Username, greenmail.Password, CancellationToken.None);
        return imap;
    }

    private async Task DeliverAsync(string subject, bool markSeen)
    {
        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync(greenmail.Host, greenmail.SmtpPort, SecureSocketOptions.None, CancellationToken.None);
            await smtp.AuthenticateAsync(greenmail.Username, greenmail.Password, CancellationToken.None);
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
            message.To.Add(new MailboxAddress("GreenMail", greenmail.Username));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = "hello from GreenMail" };
            await smtp.SendAsync(message, CancellationToken.None);
            await smtp.DisconnectAsync(true, CancellationToken.None);
        }

        using var imap = await ConnectAsync();
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, CancellationToken.None);
        var uids = await WaitForSubjectAsync(inbox, subject);
        if (markSeen)
            await inbox.AddFlagsAsync(uids[0], MessageFlags.Seen, true, CancellationToken.None);
        await imap.DisconnectAsync(true, CancellationToken.None);
    }

    private static async Task<IList<UniqueId>> WaitForSubjectAsync(IMailFolder inbox, string subject)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var summaries = await inbox.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, CancellationToken.None);
            var uid = summaries.SingleOrDefault(item => item.Envelope?.Subject == subject)?.UniqueId;
            if (uid is { IsValid: true }) return [uid.Value];
            var seenSubjects = string.Join(" | ", summaries.Select(item => item.Envelope?.Subject ?? "<none>"));
            Assert.True(DateTime.UtcNow < deadline,
                $"GreenMail did not deliver '{subject}' within 20 seconds. count={summaries.Count} subjects=[{seenSubjects}]");
            await Task.Delay(250);
        }
    }

    private static async Task<(Guid AccountId, Guid FolderId)> SeedAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "test@localhost",
            NormalizedEmailAddress = $"TEST-{accountId:N}@LOCALHOST",
            Username = "test@localhost",
            ImapHost = "127.0.0.1",
            ImapPort = 3143,
            SmtpHost = "127.0.0.1",
            SmtpPort = 3025,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsSyncEnabled = true,
            IsAvailable = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId);
    }

    private static MailFolderSyncService CreateSyncService(AppDbContext db) => new(
        db,
        new MailCredentialResolver(db, new PassthroughProtector()),
        new MailConnectionHelper(new OutboundHostValidator(new FakeDns(System.Net.IPAddress.Loopback)), NullLogger<MailConnectionHelper>.Instance),
        new FakeFileStorage(),
        new MailSyncOptions
        {
            Enabled = true,
            PollIntervalSeconds = 30,
            FlagSyncIntervalSeconds = 120,
            MaxMessagesPerRun = 100,
            MaxAttachmentBytes = 1024 * 1024,
            MaxMessageAttachmentBytes = 10 * 1024 * 1024,
            MaxMessageBytes = 100 * 1024 * 1024
        },
        new FakePushNotificationService(),
        NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
