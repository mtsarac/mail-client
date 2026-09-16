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
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace MailClient.Tests;

[CollectionDefinition("greenmail")]
public sealed class GreenMailCollection : ICollectionFixture<GreenMailFixture>;

/// <summary>
/// GreenMail is a throwaway local test server. Only its plaintext ports are published and the
/// production connection policy forbids plaintext, so the fixture drives the real IMAP protocol
/// with a raw MailKit client and then exercises the production sync core against it. Messages are
/// seeded with IMAP APPEND, so no recipient routing or SMTP credentials are involved.
/// </summary>
public sealed class GreenMailFixture : IAsyncLifetime
{
    public string Host => IntegrationEnvironment.GreenMailHost;
    public int ImapPort => IntegrationEnvironment.GreenMailImapPort;
    public string Username => IntegrationEnvironment.GreenMailUsername;
    public string Password => IntegrationEnvironment.GreenMailPassword;

    public async Task InitializeAsync()
    {
        if (!IntegrationEnvironment.GreenMailEnabled) return;
        if (!await ProbeAsync(ImapPort))
            throw new InvalidOperationException(
                $"GreenMail is not reachable at {Host}:{ImapPort}. Start it or set MAILCLIENT_SKIP_INTEGRATION=1.");
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
        await SeedMessageAsync(unread, markSeen: false);
        await SeedMessageAsync(seen, markSeen: true);

        using var imap = await ConnectAsync();
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, CancellationToken.None);
        var remote = new MailKitRemoteMailFolder(inbox);

        await using var db = NewDb();
        var (accountId, folderId) = await SeedAccountAsync(db);
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

        await remote.AppendAsync(Message(subject), CancellationToken.None);

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

    [Fact]
    public async Task RealImap_Phase4RemoteFolder_SupportsFlagsCopyAndMove()
    {
        if (!IntegrationEnvironment.GreenMailEnabled) return;
        var subject = $"phase4-{Guid.NewGuid():N}";
        using var imap = await ConnectAsync();
        var inbox = await imap.GetFolderAsync("INBOX", CancellationToken.None);
        IMailFolder archive;
        try
        {
            archive = await imap.GetFolderAsync("Phase4Archive", CancellationToken.None);
        }
        catch (FolderNotFoundException)
        {
            await imap.GetFolder(imap.PersonalNamespaces[0].Path).CreateAsync("Phase4Archive", true, CancellationToken.None);
            archive = await imap.GetFolderAsync("Phase4Archive", CancellationToken.None);
        }
        var remote = new MailKitRemoteMailFolder(inbox, imap.GetFolderAsync);
        await remote.OpenForUpdateAsync(CancellationToken.None);
        await remote.AppendAsync(Message(subject), CancellationToken.None);
        var sourceUid = Assert.Single(await inbox.SearchAsync(SearchQuery.SubjectContains(subject), CancellationToken.None));
        await remote.SetSeenAsync(sourceUid, true, CancellationToken.None);
        await remote.SetFlaggedAsync(sourceUid, true, CancellationToken.None);
        var copy = await remote.CopyAsync(sourceUid, archive.FullName, CancellationToken.None);
        await imap.DisconnectAsync(true, CancellationToken.None);

        using var moveImap = await ConnectAsync();
        var moveInbox = await moveImap.GetFolderAsync("INBOX", CancellationToken.None);
        var moveRemote = new MailKitRemoteMailFolder(moveInbox, moveImap.GetFolderAsync);
        await moveRemote.OpenForUpdateAsync(CancellationToken.None);
        var move = await moveRemote.MoveAsync(sourceUid, archive.FullName, CancellationToken.None);
        await moveImap.DisconnectAsync(true, CancellationToken.None);

        using var verifyImap = await ConnectAsync();
        var verifyArchive = await verifyImap.GetFolderAsync("Phase4Archive", CancellationToken.None);
        await verifyArchive.OpenAsync(FolderAccess.ReadOnly, CancellationToken.None);
        var destinationMessages = await verifyArchive.SearchAsync(SearchQuery.SubjectContains(subject), CancellationToken.None);
        Assert.Equal(2, destinationMessages.Count);
        await verifyImap.DisconnectAsync(true, CancellationToken.None);
    }

    private async Task<ImapClient> ConnectAsync()
    {
        var imap = new ImapClient();
        await imap.ConnectAsync(greenmail.Host, greenmail.ImapPort, SecureSocketOptions.None, CancellationToken.None);
        await imap.AuthenticateAsync(greenmail.Username, greenmail.Password, CancellationToken.None);
        return imap;
    }

    private async Task SeedMessageAsync(string subject, bool markSeen)
    {
        using var imap = await ConnectAsync();
        var inbox = await imap.GetFolderAsync("INBOX", CancellationToken.None);
        var remote = new MailKitRemoteMailFolder(inbox);
        await remote.OpenForUpdateAsync(CancellationToken.None);
        await remote.AppendAsync(Message(subject), CancellationToken.None);
        var uids = await inbox.SearchAsync(SearchQuery.SubjectContains(subject), CancellationToken.None);
        if (markSeen)
            await inbox.AddFlagsAsync(uids[0], MessageFlags.Seen, true, CancellationToken.None);
        else
            await inbox.RemoveFlagsAsync(uids[0], MessageFlags.Seen, true, CancellationToken.None);
        await imap.DisconnectAsync(true, CancellationToken.None);
    }

    private static MimeMessage Message(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("GreenMail", "test@localhost"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "greenmail body" };
        return message;
    }

    private async Task<(Guid AccountId, Guid FolderId)> SeedAccountAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "test@localhost",
            NormalizedEmailAddress = $"TEST-{accountId:N}@LOCALHOST",
            Username = greenmail.Username,
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
        new MailClient.Infrastructure.Services.ConversationService(db), new MailReconciliationService(db), NullLogger<MailFolderSyncService>.Instance);

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
