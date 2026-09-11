using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class MailSendServiceTests
{
    private static MailSyncOptions Options() => new()
    {
        Enabled = true,
        PollIntervalSeconds = 30,
        FlagSyncIntervalSeconds = 120,
        MaxMessagesPerRun = 10,
        MaxAttachmentBytes = 10,
        MaxMessageAttachmentBytes = 20,
        MaxMessageBytes = 1000
    };

    private static SendMailCommand Command(
        Guid accountId,
        string to = "friend@example.test",
        string subject = "Hello",
        string? html = "<p>hi</p>",
        string? text = "hi",
        IReadOnlyList<SendMailAttachment>? attachments = null,
        string? key = null)
    {
        var command = new SendMailCommand(accountId, to, subject, html, text, attachments ?? []);
        return command with { IdempotencyKey = key ?? $"key-{Guid.NewGuid():N}" };
    }

    [Fact]
    public async Task Send_MissingIdempotencyKey_RejectedWithoutSend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport();
        var service = CreateService(db, transport);
        var command = new SendMailCommand(accountId, "friend@example.test", "Hi", null, "hello", []);

        var result = await service.SendAsync(userId, command, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Contains("idempotencyKey", result.Errors.Keys);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Send_OwnAccount_AppendsSameMessageToSent()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, saveSentCopy: true, withSentFolder: true);
        var transport = new FakeMailTransport();
        var service = CreateService(db, transport);

        var result = await service.SendAsync(userId, Command(accountId), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        Assert.True(result.Value!.Sent);
        Assert.True(result.Value.SentCopySaved);
        Assert.Null(result.Value.Warning);
        Assert.Single(transport.Sent);
        Assert.Single(transport.Appended);
        Assert.Same(transport.Sent[0], transport.Appended[0].Message);
        Assert.Equal("INBOX.Sent", transport.Appended[0].FullName);
        var sent = transport.Sent[0];
        Assert.Equal("me@example.test", sent.From.Mailboxes.Single().Address);
        Assert.Equal("friend@example.test", sent.To.Mailboxes.Single().Address);
    }

    [Fact]
    public async Task Send_OtherUsersAccount_ReturnsNotFoundWithoutSend()
    {
        await using var db = CreateDb();
        var (_, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport();

        var result = await CreateService(db, transport)
            .SendAsync(Guid.NewGuid(), Command(accountId), CancellationToken.None);

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Send_InactiveAccount_ReturnsNotFound()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, isActive: false);
        var transport = new FakeMailTransport();

        var result = await CreateService(db, transport)
            .SendAsync(userId, Command(accountId), CancellationToken.None);

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
        Assert.Empty(transport.Sent);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Send_InvalidRecipient_RejectedWithoutSend(string to)
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport();

        var result = await CreateService(db, transport)
            .SendAsync(userId, Command(accountId, to: to), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Send_MissingBody_RejectedWithoutSend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport();

        var result = await CreateService(db, transport)
            .SendAsync(userId, Command(accountId, html: null, text: "  "), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Send_SmtpFailure_ReturnsNotSent_WithoutAppend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport
        {
            SendFailure = () => new MailConnectionException(MailConnectionFailure.Network, "unreachable")
        };

        var result = await CreateService(db, transport)
            .SendAsync(userId, Command(accountId), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        Assert.False(result.Value!.Sent);
        Assert.False(result.Value.SentCopySaved);
        Assert.NotNull(result.Value.Warning);
        Assert.Empty(transport.Appended);
    }

    [Fact]
    public async Task Send_ErrorMessage_NeverContainsMailboxPassword()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport
        {
            SendFailure = () => new InvalidOperationException("auth failed for s3cret-password")
        };

        var result = await CreateService(db, transport)
            .SendAsync(userId, Command(accountId), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Conflict, result.Outcome);
        Assert.DoesNotContain("s3cret-password",
            string.Join(";", result.Errors.SelectMany(entry => entry.Value)));
    }

    [Fact]
    public async Task Send_SaveSentCopyFalse_SkipsAppend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, saveSentCopy: false, withSentFolder: true);
        var transport = new FakeMailTransport();

        var result = await CreateService(db, transport)
            .SendAsync(userId, Command(accountId), CancellationToken.None);

        Assert.True(result.Value!.Sent);
        Assert.False(result.Value.SentCopySaved);
        Assert.Null(result.Value.Warning);
        Assert.Empty(transport.Appended);
    }

    [Fact]
    public async Task Send_AppendFailure_KeepsSentTrue_SendsExactlyOnce()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, withSentFolder: true);
        var transport = new FakeMailTransport
        {
            AppendFailure = () => new MailConnectionException(MailConnectionFailure.Protocol, "append denied")
        };

        var result = await CreateService(db, transport)
            .SendAsync(userId, Command(accountId), CancellationToken.None);

        Assert.True(result.Value!.Sent);
        Assert.False(result.Value.SentCopySaved);
        Assert.NotNull(result.Value.Warning);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task Send_MissingSentFolder_SucceedsWithoutCopy()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, withSentFolder: false);
        var transport = new FakeMailTransport();

        var result = await CreateService(db, transport)
            .SendAsync(userId, Command(accountId), CancellationToken.None);

        Assert.True(result.Value!.Sent);
        Assert.False(result.Value.SentCopySaved);
        Assert.NotNull(result.Value.Warning);
        Assert.Empty(transport.Appended);
    }

    [Fact]
    public async Task Send_AttachmentsIncluded_WithContent()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, withSentFolder: false);
        var transport = new FakeMailTransport();
        var first = new MemoryStream("11111"u8.ToArray());
        var second = new MemoryStream("22222"u8.ToArray());

        var result = await CreateService(db, transport).SendAsync(
            userId,
            Command(accountId, attachments:
            [
                new SendMailAttachment("a.txt", "text/plain", first),
                new SendMailAttachment("b.txt", "text/plain", second)
            ]),
            CancellationToken.None);

        Assert.True(result.Value!.Sent);
        var mixed = Assert.IsType<Multipart>(transport.Sent[0].Body);
        Assert.Equal(3, mixed.Count);
        Assert.Equal("a.txt", Assert.IsType<MimePart>(mixed[1]).FileName);
        Assert.Equal("b.txt", Assert.IsType<MimePart>(mixed[2]).FileName);
    }

    [Fact]
    public async Task Send_OversizedAttachments_Rejected()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport();
        using var big = new MemoryStream(new byte[11]);
        using var small = new MemoryStream(new byte[10]);

        var perFile = await CreateService(db, transport).SendAsync(
            userId, Command(accountId, attachments: [new SendMailAttachment("big.bin", "application/octet-stream", big)]),
            CancellationToken.None);
        Assert.Equal(ServiceOutcome.Invalid, perFile.Outcome);

        using var first = new MemoryStream(new byte[10]);
        using var second = new MemoryStream(new byte[10]);
        using var third = new MemoryStream(new byte[10]);
        var perMessage = await CreateService(db, transport).SendAsync(
            userId,
            Command(accountId, attachments:
            [
                new SendMailAttachment("1.bin", "application/octet-stream", first),
                new SendMailAttachment("2.bin", "application/octet-stream", second),
                new SendMailAttachment("3.bin", "application/octet-stream", third)
            ]),
            CancellationToken.None);
        Assert.Equal(ServiceOutcome.Invalid, perMessage.Outcome);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Send_Cancelled_Propagates()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(db, new FakeMailTransport()).SendAsync(
                userId, Command(accountId), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Send_InvalidRecipient_DisposesAttachmentStreams()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var stream = new TrackingStream("abc"u8.ToArray());

        await CreateService(db, new FakeMailTransport()).SendAsync(
            userId, Command(accountId, to: "not-an-address",
                attachments: [new SendMailAttachment("a.txt", "text/plain", stream)]),
            CancellationToken.None);

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Send_AccountNotFound_DisposesAttachmentStreams()
    {
        await using var db = CreateDb();
        await SeedAccountAsync(db);
        var stream = new TrackingStream("abc"u8.ToArray());

        await CreateService(db, new FakeMailTransport()).SendAsync(
            Guid.NewGuid(),
            Command(Guid.NewGuid(), attachments: [new SendMailAttachment("a.txt", "text/plain", stream)]),
            CancellationToken.None);

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Send_OversizedAttachment_DisposesAttachmentStreams()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var stream = new TrackingStream(new byte[11]);

        await CreateService(db, new FakeMailTransport()).SendAsync(
            userId, Command(accountId, attachments: [new SendMailAttachment("big.bin", "application/octet-stream", stream)]),
            CancellationToken.None);

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Send_SmtpFailure_DisposesAttachmentStreams()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, withSentFolder: false);
        var stream = new TrackingStream("abc"u8.ToArray());
        var transport = new FakeMailTransport
        {
            SendFailure = () => new MailConnectionException(MailConnectionFailure.Network, "unreachable")
        };

        await CreateService(db, transport).SendAsync(
            userId, Command(accountId, attachments: [new SendMailAttachment("a.txt", "text/plain", stream)]),
            CancellationToken.None);

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Send_Success_DisposesAttachmentStreams()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, withSentFolder: false);
        var stream = new TrackingStream("abc"u8.ToArray());

        var result = await CreateService(db, new FakeMailTransport()).SendAsync(
            userId, Command(accountId, attachments: [new SendMailAttachment("a.txt", "text/plain", stream)]),
            CancellationToken.None);

        Assert.True(result.Value!.Sent);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Send_Cancelled_DisposesAttachmentStreams()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var stream = new TrackingStream("abc"u8.ToArray());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(db, new FakeMailTransport()).SendAsync(
                userId,
                Command(accountId, attachments: [new SendMailAttachment("a.txt", "text/plain", stream)]),
                new CancellationToken(canceled: true)));

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Send_LongSubject_TruncatedToFieldLimit()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db, withSentFolder: false);
        var transport = new FakeMailTransport();

        var result = await CreateService(db, transport).SendAsync(
            userId, Command(accountId, subject: new string('s', 600)), CancellationToken.None);

        Assert.True(result.Value!.Sent);
        Assert.Equal(500, transport.Sent[0].Subject!.Length);
    }

    [Fact]
    public async Task Send_OversizedBody_Rejected()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport();

        var result = await CreateService(db, transport).SendAsync(
            userId, Command(accountId, html: null, text: new string('x', 1_000_001)),
            CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Send_TooManyAttachments_Rejected_AndStreamsDisposed()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new FakeMailTransport();
        var attachments = Enumerable.Range(0, 21)
            .Select(index => new SendMailAttachment(
                $"f{index}.txt", "text/plain", new TrackingStream("x"u8.ToArray())))
            .ToList();

        var result = await CreateService(db, transport).SendAsync(
            userId, Command(accountId, attachments: attachments), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Empty(transport.Sent);
        Assert.All(attachments, attachment => Assert.True(((TrackingStream)attachment.Content).Disposed));
    }

    private static MailSendService CreateService(AppDbContext db, IMailTransport transport) =>
        new(db, transport, new SendOperationStore(db, NullLogger<SendOperationStore>.Instance),
            Options(), NullLogger<MailSendService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<(Guid UserId, Guid AccountId)> SeedAccountAsync(
        AppDbContext db, bool saveSentCopy = true, bool withSentFolder = true, bool isActive = true)
    {
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"sender-{userId:N}@example.test",
            PasswordHash = "seed",
            DisplayName = "Sender"
        });
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            UserId = userId,
            EmailAddress = "me@example.test",
            DisplayName = "Me",
            Username = "me",
            EncryptedPassword = "x",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            SaveSentCopy = saveSentCopy,
            IsActive = isActive
        });
        if (withSentFolder)
            db.MailFolders.Add(new MailFolder
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                Name = "Sent",
                FullName = "INBOX.Sent",
                FolderType = MailFolderType.Sent,
                IsSyncEnabled = true
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (userId, accountId);
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    internal sealed class FakeMailTransport : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];
        public List<(string FullName, MimeMessage Message)> Appended { get; } = [];
        public Func<Exception>? SendFailure { get; set; }
        public Func<Exception>? AppendFailure { get; set; }
        public bool ThrowCanceledAfterSend { get; set; }
        public bool ThrowUnknownAfterSend { get; set; }
        public Func<CancellationToken, Task>? OnAppending { get; set; }
        public Func<CancellationTokenSource, Task>? OnSendingAsync { get; set; }

        public async Task SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken)
        {
            if (SendFailure is not null)
                throw SendFailure();
            Sent.Add(message);
            if (OnSendingAsync is not null)
            {
                var cts = new CancellationTokenSource();
                try
                {
                    await OnSendingAsync(cts);
                }
                finally
                {
                    cts.Dispose();
                }
            }
            if (ThrowCanceledAfterSend)
                throw new OperationCanceledException();
            if (ThrowUnknownAfterSend)
                throw new SmtpDeliveryException("SMTP delivery outcome is unknown.");
        }

        public Task AppendToSentAsync(MailAccount account, string sentFullName, MimeMessage message, CancellationToken cancellationToken)
        {
            if (AppendFailure is not null)
                throw AppendFailure();
            if (OnAppending is not null)
                return OnAppending(cancellationToken);
            Appended.Add((sentFullName, message));
            return Task.CompletedTask;
        }
    }
}
