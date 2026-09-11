using System.Text;
using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class SendIdempotencyTests
{
    private static MailSyncOptions Options() => new()
    {
        Enabled = true,
        PollIntervalSeconds = 30,
        FlagSyncIntervalSeconds = 120,
        MaxMessagesPerRun = 10,
        MaxAttachmentBytes = 1024,
        MaxMessageAttachmentBytes = 4096,
        MaxMessageBytes = 100000
    };

    [Fact]
    public async Task SameKey_Twice_SendsOnce_SecondReplaysStoredResult()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport();
        var service = CreateService(db, transport);
        var command = KeyedCommand(accountId, "key-1");

        var first = await service.SendAsync(userId, command, CancellationToken.None);
        var second = await service.SendAsync(userId, command, CancellationToken.None);

        Assert.True(first.Value!.Sent);
        Assert.True(first.Value.SentCopySaved);
        Assert.True(second.Value!.Sent);
        Assert.True(second.Value.SentCopySaved);
        Assert.Null(second.Value.Warning);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task SameKey_DifferentRequest_ReturnsConflict_WithoutResend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport();
        var service = CreateService(db, transport);

        var first = await service.SendAsync(userId, KeyedCommand(accountId, "key-1"), CancellationToken.None);
        var second = await service.SendAsync(
            userId, KeyedCommand(accountId, "key-1") with { Subject = "Different" }, CancellationToken.None);

        Assert.True(first.Value!.Sent);
        Assert.Equal(ServiceOutcome.Conflict, second.Outcome);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task AppendFailure_Retry_ReplaysWithoutResend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport
        {
            AppendFailure = () => new MailConnectionException(MailConnectionFailure.Protocol, "append denied")
        };
        var service = CreateService(db, transport);
        var command = KeyedCommand(accountId, "key-1");

        var first = await service.SendAsync(userId, command, CancellationToken.None);
        var second = await service.SendAsync(userId, command, CancellationToken.None);

        Assert.True(first.Value!.Sent);
        Assert.False(first.Value.SentCopySaved);
        Assert.True(second.Value!.Sent);
        Assert.False(second.Value.SentCopySaved);
        Assert.Equal(first.Value.Warning, second.Value.Warning);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task InterruptionAfterSmtpSuccess_Retry_DoesNotResend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport { ThrowCanceledAfterSend = true };
        var service = CreateService(db, transport);
        var command = KeyedCommand(accountId, "key-1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SendAsync(userId, command, CancellationToken.None));
        var retry = await service.SendAsync(userId, command, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Conflict, retry.Outcome);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task FailedBeforeSend_Retry_RetriesSmtp()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport
        {
            SendFailure = () => new MailConnectionException(MailConnectionFailure.Network, "unreachable")
        };
        var service = CreateService(db, transport);
        var command = KeyedCommand(accountId, "key-1");

        var first = await service.SendAsync(userId, command, CancellationToken.None);
        Assert.False(first.Value!.Sent);

        transport.SendFailure = null;
        var retry = await service.SendAsync(userId, command, CancellationToken.None);

        Assert.True(retry.Value!.Sent);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task SameKey_DifferentUsers_AreIndependent()
    {
        await using var db = CreateDb();
        var (userA, accountA) = await SeedAccountAsync(db);
        var (userB, accountB) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport();
        var service = CreateService(db, transport);

        var first = await service.SendAsync(userA, KeyedCommand(accountA, "shared"), CancellationToken.None);
        var second = await service.SendAsync(userB, KeyedCommand(accountB, "shared"), CancellationToken.None);

        Assert.True(first.Value!.Sent);
        Assert.True(second.Value!.Sent);
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public async Task SameMetadata_DifferentBytes_ReturnsConflict()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport();
        var service = CreateService(db, transport);

        var first = await service.SendAsync(
            userId, KeyedCommand(accountId, "key-1", "AAA"), CancellationToken.None);
        var second = await service.SendAsync(
            userId, KeyedCommand(accountId, "key-1", "BBB"), CancellationToken.None);

        Assert.True(first.Value!.Sent);
        Assert.Equal(ServiceOutcome.Conflict, second.Outcome);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task SameBytes_Replays_WithoutResend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport();
        var service = CreateService(db, transport);

        var first = await service.SendAsync(
            userId, KeyedCommand(accountId, "key-1", "SAME"), CancellationToken.None);
        var second = await service.SendAsync(
            userId, KeyedCommand(accountId, "key-1", "SAME"), CancellationToken.None);

        Assert.True(first.Value!.Sent);
        Assert.True(second.Value!.Sent);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task SentPersisted_BeforeAppend_Runs()
    {
        var options = CreateOptions();
        await using var db = new AppDbContext(options);
        var (userId, accountId) = await SeedAccountAsync(db);
        var statusAtAppend = new TaskCompletionSource<SendOperationStatus>();
        var transport = new MailSendServiceTests.FakeMailTransport
        {
            OnAppending = _ =>
            {
                using var check = new AppDbContext(options);
                statusAtAppend.SetResult(check.SendOperations.Single().Status);
                return Task.CompletedTask;
            }
        };
        var service = CreateService(db, transport);

        var result = await service.SendAsync(userId, KeyedCommand(accountId, "key-1"), CancellationToken.None);

        Assert.True(result.Value!.Sent);
        Assert.Equal(SendOperationStatus.Sent, await statusAtAppend.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DeliveryUnknown_Retry_DoesNotResend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport { ThrowUnknownAfterSend = true };
        var service = CreateService(db, transport);
        var command = KeyedCommand(accountId, "key-1");

        var first = await service.SendAsync(userId, command, CancellationToken.None);
        var retry = await service.SendAsync(userId, command, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Conflict, first.Outcome);
        Assert.Equal(ServiceOutcome.Conflict, retry.Outcome);
        Assert.Single(transport.Sent);
        Assert.Equal(SendOperationStatus.DeliveryUnknown,
            (await db.SendOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task StaleInProgress_DoesNotResend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport { ThrowCanceledAfterSend = true };
        var service = CreateService(db, transport);
        var command = KeyedCommand(accountId, "key-1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SendAsync(userId, command, CancellationToken.None));
        var operation = await db.SendOperations.SingleAsync();
        operation.UpdatedAt = operation.UpdatedAt.AddHours(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var retry = await service.SendAsync(userId, command, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Conflict, retry.Outcome);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task RealRequestCancellation_PersistsDeliveryUnknown_AndRetryDoesNotResend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport();
        var observedCancelToken = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        try
        {
            transport.OnSendingAsync = async cts =>
            {
                cts.Cancel();
                await Task.CompletedTask;
                throw new OperationCanceledException(cts.Token);
            };
            var service = CreateService(db, transport);
            var command = KeyedCommand(accountId, "key-1");

            // The request token is already cancelled when SMTP aborts, like a real
            // HTTP disconnect: the safety write must still persist DeliveryUnknown.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SendAsync(userId, command, observedCancelToken.Token));

            var operation = await db.SendOperations.SingleAsync();
            Assert.Equal(SendOperationStatus.DeliveryUnknown, operation.Status);

            var retry = await service.SendAsync(userId, command, CancellationToken.None);

            Assert.Equal(ServiceOutcome.Conflict, retry.Outcome);
            Assert.Single(transport.Sent);
        }
        finally
        {
            observedCancelToken.Dispose();
        }
    }

    [Fact]
    public async Task SafetyWriteFailure_StillPropagatesCancellation_AndRetryDoesNotResend()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var transport = new MailSendServiceTests.FakeMailTransport();
        var db = CreateDb(dbName);
        var (userId, accountId) = await SeedAccountAsync(db);
        transport.OnSendingAsync = async cts =>
        {
            cts.Cancel();
            await db.DisposeAsync();
            throw new OperationCanceledException(cts.Token);
        };
        var service = CreateService(db, transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SendAsync(userId, KeyedCommand(accountId, "key-1"), CancellationToken.None));

        await using (var check = CreateDb(dbName))
        {
            var operation = await check.SendOperations.SingleAsync();
            Assert.Equal(SendOperationStatus.InProgress, operation.Status);
            var retry = await CreateService(check, transport)
                .SendAsync(userId, KeyedCommand(accountId, "key-1"), CancellationToken.None);

            Assert.Equal(ServiceOutcome.Conflict, retry.Outcome);
            Assert.Single(transport.Sent);
        }
    }

    [Fact]
    public async Task NoKey_RejectedWithoutSend()
    {
        await using var db = CreateDb();
        var (userId, accountId) = await SeedAccountAsync(db);
        var transport = new MailSendServiceTests.FakeMailTransport();
        var service = CreateService(db, transport);
        var command = new SendMailCommand(accountId, "friend@example.test", "Hi", null, "hello", []);

        var result = await service.SendAsync(userId, command, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Fingerprint_IsDeterministic_AndSensitiveToFields()
    {
        var accountId = Guid.NewGuid();
        List<(string, string, long, string)> attachments = [("a.txt", "text/plain", 3, "HASH1")];
        var first = SendOperationStore.Fingerprint(accountId, "a@example.test", "Hi", null, "x", attachments);
        var same = SendOperationStore.Fingerprint(accountId, "a@example.test", "Hi", null, "x", attachments);
        var differentSubject = SendOperationStore.Fingerprint(accountId, "a@example.test", "Changed", null, "x", attachments);
        List<(string, string, long, string)> differentBytes = [("a.txt", "text/plain", 3, "HASH2")];
        var differentContent = SendOperationStore.Fingerprint(accountId, "a@example.test", "Hi", null, "x", differentBytes);

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentSubject);
        Assert.NotEqual(first, differentContent);
        Assert.Equal(64, first.Length);
    }

    private static SendMailCommand KeyedCommand(Guid accountId, string key) =>
        new SendMailCommand(accountId, "friend@example.test", "Hello", "<p>hi</p>", "hi", []) with
        {
            IdempotencyKey = key
        };

    private static SendMailCommand KeyedCommand(Guid accountId, string key, string attachmentText) =>
        new SendMailCommand(accountId, "friend@example.test", "Hello", "<p>hi</p>", "hi",
            [new SendMailAttachment("doc.txt", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(attachmentText)))]) with
        {
            IdempotencyKey = key
        };

    private static MailSendService CreateService(AppDbContext db, MailSendServiceTests.FakeMailTransport transport) =>
        new(db, transport, new SendOperationStore(db, NullLogger<SendOperationStore>.Instance),
            Options(), NullLogger<MailSendService>.Instance);

    private static AppDbContext CreateDb(string name) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(name).Options);

    private static AppDbContext CreateDb() => new(CreateOptions());

    private static DbContextOptions<AppDbContext> CreateOptions() => new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

    private static async Task<(Guid UserId, Guid AccountId)> SeedAccountAsync(AppDbContext db)
    {
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"idem-{userId:N}@example.test",
            PasswordHash = "seed",
            DisplayName = "Idem"
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
            SaveSentCopy = true,
            IsActive = true
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Name = "Sent",
            FullName = "Sent",
            FolderType = MailFolderType.Sent,
            IsSyncEnabled = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (userId, accountId);
    }
}
