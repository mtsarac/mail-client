using System.Net;
using System.Net.Sockets;
using System.Text;
using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace MailClient.Tests;

public sealed class SendServiceTests
{
    [Fact]
    public void Fingerprint_IsDeterministic_AndSensitiveToFields()
    {
        var accountId = Guid.NewGuid();
        List<(string, string, long, string)> attachments = [("a.txt", "text/plain", 3, "HASH1")];
        var first = SendOperationStore.Fingerprint(accountId, "a@example.test", "Hi", null, "x", attachments);
        var same = SendOperationStore.Fingerprint(accountId, "a@example.test", "Hi", null, "x", attachments);
        var differentSubject = SendOperationStore.Fingerprint(accountId, "a@example.test", "Changed", null, "x", attachments);

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentSubject);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Fingerprint_PreservesRecipientCategoriesAndDisplayNames()
    {
        var accountId = Guid.NewGuid();
        var toAndCc = SendOperationStore.Fingerprint(accountId,
            [new MailboxAddress("Alice", "a@example.test")], [new MailboxAddress("Bob", "b@example.test")], [], "Hi", null, "x", []);
        var toOnly = SendOperationStore.Fingerprint(accountId,
            [new MailboxAddress("Alice", "a@example.test"), new MailboxAddress("Bob", "b@example.test")], [], [], "Hi", null, "x", []);
        var differentName = SendOperationStore.Fingerprint(accountId,
            [new MailboxAddress("Alicia", "a@example.test")], [new MailboxAddress("Bob", "b@example.test")], [], "Hi", null, "x", []);

        Assert.NotEqual(toAndCc, toOnly);
        Assert.NotEqual(toAndCc, differentName);
    }

    [Fact]
    public async Task Claim_Replay_AndConflict_StateMachine()
    {
        await using var db = CreateDb();
        var accountId = Guid.NewGuid();
        var store = CreateStore(db);
        var fingerprint = SendOperationStore.Fingerprint(accountId, "a@example.test", "Hi", null, "x", []);

        Assert.IsType<SendOperationStore.Proceed>(await store.ClaimAsync(accountId, "key-1", fingerprint, CancellationToken.None));
        Assert.IsType<SendOperationStore.Denied>(await store.ClaimAsync(accountId, "key-1", fingerprint, CancellationToken.None));
        Assert.IsType<SendOperationStore.Denied>(await store.ClaimAsync(accountId, "key-1", fingerprint + "00", CancellationToken.None));

        var operation = await db.SendOperations.SingleAsync();
        await store.TryCompleteAsync(operation.Id, SendOperationStatus.Sent, false, null, CancellationToken.None);
        var replay = Assert.IsType<SendOperationStore.Replay>(await store.ClaimAsync(accountId, "key-1", fingerprint, CancellationToken.None));
        Assert.Equal(operation.Id, replay.Operation.Id);
    }

    [Fact]
    public async Task SendAsync_Success_SendsAndSavesSentCopyWithAudit()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: true);
        var transport = new FakeMailTransport();
        var service = CreateService(db, transport);
        var command = new SendMailCommand(accountId, "friend@example.test", "Hello", null, "body", [])
        {
            IdempotencyKey = "send-1"
        };

        var result = await service.SendAsync(accountId, command, "corr-send", CancellationToken.None);

        Assert.True(result is { Sent: true, SentCopySaved: true, Warning: null });
        Assert.Equal(1, transport.SentCount);
        Assert.Equal(1, transport.AppendCount);
        Assert.NotNull(await db.AuditLogs.SingleOrDefaultAsync(x => x.Action == "mail.sent" && x.MailAccountId == accountId));
        var replay = await service.SendAsync(accountId, command with { }, "corr-send", CancellationToken.None);
        Assert.True(replay.Sent);
        Assert.Equal(1, transport.SentCount);
    }

    [Fact]
    public async Task SendAsync_TransportFailureBeforeDelivery_ReturnsUnsent()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: false);
        var transport = new FakeMailTransport { SendFailure = new MailConnectionException(MailConnectionFailure.Network, "down") };
        var service = CreateService(db, transport);
        var command = new SendMailCommand(accountId, "friend@example.test", "Hello", null, "body", [])
        {
            IdempotencyKey = "send-2"
        };

        var result = await service.SendAsync(accountId, command, null, CancellationToken.None);

        Assert.False(result.Sent);
    }

    [Fact]
    public async Task SendAsync_RetryAfterUncertainDelivery_ReportsDeliveryUnknown()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: false);
        var transport = new FakeMailTransport { SendFailure = new TimeoutException("no reply after DATA") };
        var service = CreateService(db, transport);
        var command = new SendMailCommand(accountId, "friend@example.test", "Hello", null, "body", []) { IdempotencyKey = "send-uncertain" };

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(accountId, command, null, CancellationToken.None));
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(accountId, command with { }, null, CancellationToken.None));

        Assert.Equal("delivery_unknown", first.Message);
        Assert.Equal("delivery_unknown", retry.Message);
    }

    [Fact]
    public async Task SendAsync_RecordsSuccessAndFailureMetricsWithoutMessageContent()
    {
        using var capture = new MetricsCapture();
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: false);
        var sent = CreateService(db, new FakeMailTransport(), capture.Metrics);
        var failed = CreateService(db, new FakeMailTransport { SendFailure = new MailConnectionException(MailConnectionFailure.Network, "down") }, capture.Metrics);

        await sent.SendAsync(accountId, new SendMailCommand(accountId, "friend@example.test", "Quarterly numbers", null, "body", []) { IdempotencyKey = "metrics-1" }, null, CancellationToken.None);
        await failed.SendAsync(accountId, new SendMailCommand(accountId, "friend@example.test", "Quarterly numbers", null, "body", []) { IdempotencyKey = "metrics-2" }, null, CancellationToken.None);

        Assert.Equal(1, capture.Sum("mailclient.mail.send", ("result", "success")));
        Assert.Equal(1, capture.Sum("mailclient.mail.send", ("result", "failure")));
        Assert.Equal(2, capture.For("mailclient.mail.send.duration").Count);
        Assert.DoesNotContain(capture.All.SelectMany(item => item.Tags.Values), value =>
            Convert.ToString(value)!.Contains("friend", StringComparison.Ordinal) || Convert.ToString(value)!.Contains("Quarterly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendAsync_ReplySource_DerivesThreadingHeaders()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: false);
        var sourceId = Guid.NewGuid();
        db.Mails.Add(new Mail
        {
            Id = sourceId,
            MailAccountId = accountId,
            MailFolderId = Guid.NewGuid(),
            MessageId = "source@example.test",
            References = "prior@example.test"
        });
        await db.SaveChangesAsync();
        var transport = new FakeMailTransport();
        var command = new SendMailCommand(accountId, ["friend@example.test"], [], [], "Re: Hello", null, "body", [], sourceId)
        {
            IdempotencyKey = "reply-1"
        };

        await CreateService(db, transport).SendAsync(accountId, command, null, CancellationToken.None);

        Assert.Equal("source@example.test", transport.Message!.InReplyTo);
        Assert.Equal(["prior@example.test", "source@example.test"], transport.Message.References);
        Assert.False(string.IsNullOrWhiteSpace(transport.Message.MessageId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_ReplyDraftSource_PreservesOriginalThreadAfterReplacement(bool retired)
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: false);
        var draftsId = Guid.NewGuid();
        var trashId = Guid.NewGuid();
        db.MailFolders.AddRange(
            new MailFolder { Id = draftsId, MailAccountId = accountId, Name = "Drafts", FullName = "Drafts", FolderType = MailFolderType.Drafts },
            new MailFolder { Id = trashId, MailAccountId = accountId, Name = "Trash", FullName = "Trash", FolderType = MailFolderType.Trash });
        var draftId = Guid.NewGuid();
        db.Mails.Add(new Mail
        {
            Id = draftId,
            MailAccountId = accountId,
            MailFolderId = retired ? trashId : draftsId,
            PreviousMailFolderId = retired ? draftsId : null,
            Draft = false,
            MessageId = "saved-draft@example.test",
            InReplyToMessageId = "original@example.test",
            References = "ancestor@example.test original@example.test"
        });
        await db.SaveChangesAsync();
        var transport = new FakeMailTransport();
        var command = new SendMailCommand(accountId, ["friend@example.test"], [], [], "Re: Original", null, "Edited reply", [], draftId)
        {
            IdempotencyKey = "reply-draft"
        };

        var result = await CreateService(db, transport).SendAsync(accountId, command, null, CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Equal("original@example.test", transport.Message!.InReplyTo);
        Assert.Equal(["ancestor@example.test", "original@example.test"], transport.Message.References);
        Assert.DoesNotContain("saved-draft@example.test", transport.Message.References);
    }

    [Fact]
    public async Task SendAsync_ReplySourceFromAnotherAccount_IsRejected()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: false);
        var otherId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = otherId,
            EmailAddress = "other@example.test",
            NormalizedEmailAddress = "OTHER@EXAMPLE.TEST",
            Username = "other",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        var sourceId = Guid.NewGuid();
        db.Mails.Add(new Mail { Id = sourceId, MailAccountId = otherId, MailFolderId = Guid.NewGuid(), MessageId = "other@example.test" });
        await db.SaveChangesAsync();
        var command = new SendMailCommand(accountId, ["friend@example.test"], [], [], "Re: Hello", null, "body", [], sourceId)
        {
            IdempotencyKey = "reply-2"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(db, new FakeMailTransport()).SendAsync(accountId, command, null, CancellationToken.None));

        Assert.Equal("mail_not_found", error.Message);
    }

    [Fact]
    public async Task SendAsync_RequestsReadReceiptOnlyWhenOptedIn()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: false);
        var transport = new FakeMailTransport();
        var service = CreateService(db, transport);
        var plain = new SendMailCommand(accountId, "friend@example.test", "Plain", null, "body", []) { IdempotencyKey = "receipts-0" };
        var requested = new SendMailCommand(accountId, "friend@example.test", "Receipt", null, "body", []) { IdempotencyKey = "receipts-1", RequestReadReceipt = true };

        Assert.True((await service.SendAsync(accountId, plain, null, CancellationToken.None)).Sent);
        Assert.False(transport.Message!.Headers.Contains("Disposition-Notification-To"));
        Assert.True((await service.SendAsync(accountId, requested, null, CancellationToken.None)).Sent);
        Assert.Equal("me@example.test", InternetAddressList.Parse(transport.Message!.Headers["Disposition-Notification-To"]!).Mailboxes.Single().Address);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendAsync(accountId, requested with { RequestReadReceipt = false }, null, CancellationToken.None));
        Assert.Equal("idempotency_conflict", conflict.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceiptSmtpClient_RequestsDsnOnlyWhenAdvertised_AndAlwaysSends(bool advertisesDsn)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunScriptedSmtpServerAsync(listener, advertisesDsn);
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("me@example.test"));
        message.To.Add(MailboxAddress.Parse("friend@example.test"));
        message.Subject = "Receipt";
        message.Body = new TextPart("plain") { Text = "body" };

        using (var client = new ReceiptSmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", port, MailKit.Security.SecureSocketOptions.None);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }

        var (recipientCommands, delivered) = await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(delivered);
        var rcpt = Assert.Single(recipientCommands);
        if (advertisesDsn)
            Assert.Contains("NOTIFY=SUCCESS,FAILURE", rcpt);
        else
            Assert.DoesNotContain("NOTIFY", rcpt);
    }

    /// <summary>Minimal plaintext SMTP peer: records RCPT commands and whether DATA was accepted.</summary>
    private static async Task<(List<string> RecipientCommands, bool Delivered)> RunScriptedSmtpServerAsync(TcpListener listener, bool advertisesDsn)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        var recipients = new List<string>();
        var delivered = false;
        await writer.WriteLineAsync("220 test ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            var verb = line.Split(' ')[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    await writer.WriteAsync(advertisesDsn ? "250-test\r\n250 DSN\r\n" : "250 test\r\n");
                    break;
                case "RCPT":
                    recipients.Add(line);
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go ahead");
                    while (await reader.ReadLineAsync() is { } data && data != ".") { }
                    delivered = true;
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return (recipients, delivered);
                default:
                    await writer.WriteLineAsync("250 OK");
                    break;
            }
        }

        return (recipients, delivered);
    }

    [Fact]
    public async Task SendAsync_InvalidRecipient_ThrowsContractCode()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db, saveSentCopy: false);
        var service = CreateService(db, new FakeMailTransport());
        var command = new SendMailCommand(accountId, "not-an-address", "Hello", null, "body", [])
        {
            IdempotencyKey = "send-3"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(accountId, command, null, CancellationToken.None));
        Assert.Equal("invalid_recipient", error.Message);
    }

    private static MailSendService CreateService(AppDbContext db, FakeMailTransport transport, Application.Observability.MailClientMetrics? metrics = null) =>
        new(db, transport, CreateStore(db), FixedRuntimeSettingsStore.Operation(), TestServices.InlineSync(), new AuditLogger(db), NullLogger<MailSendService>.Instance, metrics);

    private static SendOperationStore CreateStore(AppDbContext db) =>
        new(db, NullLogger<SendOperationStore>.Instance);

    private static async Task<Guid> SeedAccountAsync(AppDbContext db, bool saveSentCopy)
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
            SaveSentCopy = saveSentCopy
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "Sent",
            FullName = "Sent",
            FolderType = MailFolderType.Sent,
            IsSyncEnabled = true,
            IsAvailable = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return accountId;
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
