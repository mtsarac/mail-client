using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace MailClient.Infrastructure.Tests.Services;

[CollectionDefinition("greenmail")]
public sealed class GreenMailCollection : ICollectionFixture<GreenMailFixture>;

public sealed class GreenMailFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("greenmail/standalone:2.1.8")
        .WithPortBinding(3025, true)
        .WithPortBinding(3143, true)
        .Build();

    public int SmtpPort => _container.GetMappedPublicPort(3025);
    public int ImapPort => _container.GetMappedPublicPort(3143);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await WaitForGreetingAsync(SmtpPort, "220");
        await WaitForGreetingAsync(ImapPort, "OK");
    }

    private async Task WaitForGreetingAsync(int port, string marker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, port);
                var buffer = new byte[256];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var read = await socket.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None, timeout.Token);
                if (System.Text.Encoding.ASCII.GetString(buffer, 0, read).Contains(marker))
                    return;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException
                or OperationCanceledException
                or TimeoutException)
            {
            }

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"GreenMail did not greet on port {port}.");
            await Task.Delay(500);
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Collection("greenmail")]
public sealed class GreenMailMailTests(GreenMailFixture greenmail)
{
    [Fact]
    public async Task SmtpSend_DeliversToGreenMailMailbox()
    {
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", greenmail.SmtpPort, MailKit.Security.SecureSocketOptions.None);
        var message = MimeMessageBuilder.Build(
            "gm-sender@example.test", "GM Sender",
            new MailboxAddress("GM Receiver", "gm-receiver@example.test"),
            "greenmail delivery", null, "delivered",
            []);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        using var imap = await ConnectAsync("gm-receiver");
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(1, inbox.Count);
        Assert.Equal("greenmail delivery", (await inbox.GetMessageAsync(0)).Subject);
    }

    [Fact]
    public async Task TransportSend_ThenSync_ImportsWithFlagMapping()
    {
        var login = $"sync-{Guid.NewGuid():N}";
        await DeliverAsync(login, "unread hello", seen: false);
        var seenUid = await DeliverAsync(login, "seen hello", seen: true);

        await using var db = CreateDb();
        var (userId, accountId, folderId, uidValidity) = await SeedSyncTargetAsync(db, login);

        using var imap = new ImapClient();
        await imap.ConnectAsync("127.0.0.1", greenmail.ImapPort, MailKit.Security.SecureSocketOptions.None);
        await imap.AuthenticateAsync($"{login}@example.test", login);
        var remote = new MailKitRemoteMailFolder(await imap.GetFolderAsync("INBOX"));
        await remote.OpenAsync(CancellationToken.None);
        Assert.Equal(uidValidity, remote.UidValidity);

        await CreateSyncService(db).SyncFolderCoreAsync(accountId, folderId, remote, CancellationToken.None);
        await imap.DisconnectAsync(true);

        var mails = await db.Mails.OrderBy(item => item.Uid).ToListAsync();
        Assert.Equal(2, mails.Count);
        Assert.Equal("unread hello", mails[0].Subject);
        Assert.False(mails[0].IsRead);
        Assert.Equal("seen hello", mails[1].Subject);
        Assert.True(mails[1].IsRead);
        Assert.Equal(seenUid.Id, mails[1].Uid);
    }

    [Fact]
    public async Task PatchRead_FlipsSeenFlagOnServer()
    {
        var login = $"patch-{Guid.NewGuid():N}";
        var uid = await DeliverAsync(login, "flag me", seen: false);

        await using var db = CreateDb();
        var (userId, accountId, folderId, uidValidity) = await SeedSyncTargetAsync(db, login);
        var mailId = Guid.NewGuid();
        db.Mails.Add(new Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = folderId,
            Uid = uid.Id,
            UidValidity = uidValidity,
            MessageId = $"{mailId:N}@example.test",
            Subject = "flag me",
            FromAddress = "a@example.test",
            FromDisplayName = "A",
            ToAddress = $"{login}@example.test",
            BodyText = "x",
            ReceivedAt = DateTime.UtcNow,
            IsRead = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var folders = new MailFolderClient(
            new PassthroughProtector(),
            new MailConnectionHelper(new AllowLoopbackHosts(), NullLogger<MailConnectionHelper>.Instance));
        var service = new MailReadService(db, folders, NullLogger<MailReadService>.Instance);

        var read = await service.SetReadAsync(userId, mailId, true, CancellationToken.None);
        Assert.Equal(MailClient.Application.ServiceOutcome.Ok, read.Outcome);
        Assert.True(await ServerSeenAsync(login, uid));
        Assert.True((await db.Mails.SingleAsync(item => item.Id == mailId)).IsRead);

        var unread = await service.SetReadAsync(userId, mailId, false, CancellationToken.None);
        Assert.Equal(MailClient.Application.ServiceOutcome.Ok, unread.Outcome);
        Assert.False(await ServerSeenAsync(login, uid));
    }

    private async Task<UniqueId> DeliverAsync(string login, string subject, bool seen)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync("127.0.0.1", greenmail.SmtpPort, MailKit.Security.SecureSocketOptions.None);
        var message = MimeMessageBuilder.Build(
            "a@example.test", "A",
            new MailboxAddress(login, $"{login}@example.test"),
            subject, null, subject,
            []);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);

        using var imap = await ConnectAsync(login);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var summaries = await inbox.FetchAsync(0, -1, MessageSummaryItems.UniqueId);
        var uid = summaries[^1].UniqueId;
        if (seen)
            await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true);
        return uid;
    }

    private async Task<bool> ServerSeenAsync(string login, UniqueId uid)
    {
        using var imap = await ConnectAsync(login);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        var summaries = await inbox.FetchAsync([uid], MessageSummaryItems.Flags);
        return summaries[0].Flags?.HasFlag(MessageFlags.Seen) == true;
    }

    private async Task<ImapClient> ConnectAsync(string login)
    {
        var imap = new ImapClient();
        await imap.ConnectAsync("127.0.0.1", greenmail.ImapPort, MailKit.Security.SecureSocketOptions.None);
        await imap.AuthenticateAsync($"{login}@example.test", login);
        return imap;
    }

    private async Task<(Guid UserId, Guid AccountId, Guid FolderId, uint UidValidity)> SeedSyncTargetAsync(
        AppDbContext db, string login)
    {
        using var imap = await ConnectAsync(login);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
        var uidValidity = imap.Inbox.UidValidity;

        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"{login}@example.test",
            PasswordHash = "seed",
            DisplayName = "GreenMail"
        });
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            UserId = userId,
            EmailAddress = $"{login}@example.test",
            DisplayName = login,
            Username = $"{login}@example.test",
            EncryptedPassword = login,
            ImapHost = "127.0.0.1",
            ImapPort = greenmail.ImapPort,
            ImapSecurity = MailSecurity.None,
            SmtpHost = "127.0.0.1",
            SmtpPort = greenmail.SmtpPort,
            SmtpSecurity = MailSecurity.None
        });
        db.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsSyncEnabled = true
        });
        db.SyncStates.Add(new SyncState
        {
            Id = Guid.NewGuid(),
            MailFolderId = folderId,
            UidValidity = uidValidity,
            LastUid = 0
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (userId, accountId, folderId, uidValidity);
    }

    private static MailFolderSyncService CreateSyncService(AppDbContext db) => new(
        db,
        new PassthroughProtector(),
        null!,
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

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class AllowLoopbackHosts : IOutboundHostValidator
    {
        public HostCheckResult CheckLiteralHost(string? host) => HostCheckResult.Allow();
        public Task<HostCheckResult> CheckAsync(string? host, CancellationToken cancellationToken) =>
            Task.FromResult(HostCheckResult.Allow());
        public Task<ValidatedHost> ResolveAllowedAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new ValidatedHost(host, IPAddress.Loopback));
    }
}
