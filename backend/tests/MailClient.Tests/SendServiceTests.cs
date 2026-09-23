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
