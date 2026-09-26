using System.Text.Json;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class ScheduledSendDispatcherTests
{
    [Fact]
    public async Task ProcessDueAsync_DispatchesExactlyOneDueRow_AndCallsSendPipeline()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var due = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-5), "Due now");
        var future = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddHours(1), "Not due yet");
        var transport = new FakeMailTransport();
        await using var provider = BuildProvider(db, transport, new FakeFileStorage());

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        Assert.Equal(1, transport.SentCount);
        Assert.Equal("Due now", transport.Message!.Subject);
        var dueRow = await db.ScheduledSends.AsNoTracking().SingleAsync(x => x.Id == due);
        Assert.Equal(ScheduledSendStatus.Sent, dueRow.Status);
        var futureRow = await db.ScheduledSends.AsNoTracking().SingleAsync(x => x.Id == future);
        Assert.Equal(ScheduledSendStatus.Pending, futureRow.Status);
    }

    [Fact]
    public async Task ProcessDueAsync_NetworkFailure_RetriesSameKeyAndStagedAttachmentsThenSends()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var storage = new FakeFileStorage();
        await storage.SaveAsync(accountId, Guid.NewGuid(), Guid.NewGuid(),
            (stream, ct) => stream.WriteAsync("hi"u8.ToArray(), ct).AsTask(), 1024, CancellationToken.None);
        var path = storage.Saved.Single();
        var due = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-1), "Retry",
            attachmentPath: path);
        var transport = new OnceUnavailableTransport();
        await using var provider = BuildProvider(db, transport, storage);

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);
        var pending = await db.ScheduledSends.SingleAsync(x => x.Id == due);
        Assert.Equal(ScheduledSendStatus.Pending, pending.Status);
        Assert.Equal(1, pending.AttemptCount);
        Assert.True(pending.NextAttemptAtUtc > DateTime.UtcNow);
        Assert.DoesNotContain(path, storage.Deleted);
        Assert.Single(await db.ScheduledSendAttachments.ToListAsync());
        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);
        Assert.Equal(1, transport.Attempts);

        pending.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        Assert.Equal(2, transport.Attempts);
        Assert.Equal(ScheduledSendStatus.Sent, (await db.ScheduledSends.SingleAsync(x => x.Id == due)).Status);
        Assert.Single(await db.SendOperations.ToListAsync());
        Assert.Contains(path, storage.Deleted);
    }

    [Fact]
    public async Task ProcessDueAsync_EditAfterClaim_ReturnsConflictAndLeavesDispatchedContentUnchanged()
    {
        var database = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database, root).Options;
        await using var seed = new AppDbContext(options);
        var accountId = await SeedAccountAsync(seed);
        var due = await SeedScheduledSendAsync(seed, accountId, DateTime.UtcNow.AddMinutes(-1), "Original");
        await using var editDb = new AppDbContext(options);
        await using var dispatchDb = new AppDbContext(options);
        var storage = new FakeFileStorage();
        var transport = new FakeMailTransport();
        await using var provider = BuildProvider(dispatchDb, transport, storage);
        var hook = new HookedFileStorage(storage, () => ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None));
        var service = new ScheduledSendService(editDb, hook, FixedRuntimeSettingsStore.Operation(),
            new AuditLogger(editDb), NullLogger<ScheduledSendService>.Instance);
        var edit = new ScheduledSendEdit(DateTime.UtcNow.AddHours(1), ["edited@example.test"], [], [],
            "Edited", null, "edited body", [], [File("new.txt", "new")]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdatePendingAsync(accountId, due, edit, null, CancellationToken.None));

        Assert.Equal("scheduled_send_already_sent", error.Message);
        var row = await seed.ScheduledSends.AsNoTracking().SingleAsync(x => x.Id == due);
        Assert.Equal(ScheduledSendStatus.Sent, row.Status);
        Assert.Equal("Original", row.Subject);
        Assert.Equal(0, row.Revision);
        Assert.Equal("Original", transport.Message!.Subject);
        Assert.Equal(1, transport.SentCount);
        Assert.Contains(storage.Saved.Single(), storage.Deleted);
        Assert.Empty(await seed.ScheduledSendAttachments.ToListAsync());
    }

    [Fact]
    public async Task ProcessDueAsync_EditAfterPreDeliveryFailure_UsesNewDispatchKey()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var due = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-1), "Original");
        var storage = new FakeFileStorage();
        var transport = new OnceUnavailableTransport();
        await using var provider = BuildProvider(db, transport, storage);

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);
        db.ChangeTracker.Clear();
        var service = new ScheduledSendService(db, storage, FixedRuntimeSettingsStore.Operation(),
            new AuditLogger(db), NullLogger<ScheduledSendService>.Instance);
        var edit = new ScheduledSendEdit(DateTime.UtcNow.AddHours(1), ["edited@example.test"], [], [],
            "Edited", null, "edited body", [], []);
        await service.UpdatePendingAsync(accountId, due, edit, null, CancellationToken.None);
        var row = await db.ScheduledSends.SingleAsync(x => x.Id == due);
        row.SendAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        Assert.Equal(2, transport.Attempts);
        Assert.Equal("Edited", transport.Message!.Subject);
        Assert.Equal("edited body", transport.Message.TextBody);
        Assert.Equal(ScheduledSendStatus.Sent, (await db.ScheduledSends.SingleAsync(x => x.Id == due)).Status);
        Assert.Equal(2, await db.SendOperations.CountAsync());
    }

    [Fact]
    public async Task ProcessDueAsync_AuthenticationFailure_PreservesFailedContentWithoutAutomaticRetry()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var storage = new FakeFileStorage();
        await storage.SaveAsync(accountId, Guid.NewGuid(), Guid.NewGuid(),
            (stream, ct) => stream.WriteAsync("hi"u8.ToArray(), ct).AsTask(), 1024, CancellationToken.None);
        var path = storage.Saved.Single();
        var due = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-1), "Failed",
            attachmentPath: path);
        var transport = new FakeMailTransport { SendFailure = new MailConnectionException(MailConnectionFailure.Authentication, "secret") };
        await using var provider = BuildProvider(db, transport, storage);

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);
        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        var row = await db.ScheduledSends.SingleAsync(x => x.Id == due);
        Assert.Equal(ScheduledSendStatus.Failed, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.DoesNotContain("secret", row.FailureReason ?? "");
        Assert.DoesNotContain(path, storage.Deleted);
        Assert.Single(await db.ScheduledSendAttachments.ToListAsync());
    }

    [Fact]
    public async Task ProcessDueAsync_FifthNetworkFailure_StopsRetrying()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var due = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-1), "Exhausted");
        var transport = new FakeMailTransport { SendFailure = new MailConnectionException(MailConnectionFailure.Network, "down") };
        await using var provider = BuildProvider(db, transport, new FakeFileStorage());

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);
            var row = await db.ScheduledSends.SingleAsync(x => x.Id == due);
            Assert.Equal(attempt, row.AttemptCount);
            Assert.Equal(attempt == 5 ? ScheduledSendStatus.Failed : ScheduledSendStatus.Pending, row.Status);
            if (attempt < 5)
            {
                row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);
        Assert.Equal(5, (await db.ScheduledSends.SingleAsync(x => x.Id == due)).AttemptCount);
    }

    [Fact]
    public async Task ProcessDueAsync_AmbiguousSmtpOutcome_NeverDispatchesAgain()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var due = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-1), "Unknown");
        var transport = new FakeMailTransport { SendFailure = new SmtpDeliveryException("maybe delivered") };
        await using var provider = BuildProvider(db, transport, new FakeFileStorage());

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);
        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        var row = await db.ScheduledSends.SingleAsync(x => x.Id == due);
        Assert.Equal(ScheduledSendStatus.DeliveryUnknown, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.DoesNotContain("maybe delivered", row.FailureReason ?? "");
        Assert.Single(await db.SendOperations.ToListAsync());
    }

    [Fact]
    public async Task ProcessDueAsync_ManuallyEditedFailedSend_UsesNewKeyAndDeliversEditedContent()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var storage = new FakeFileStorage();
        await storage.SaveAsync(accountId, Guid.NewGuid(), Guid.NewGuid(),
            (stream, ct) => stream.WriteAsync("hi"u8.ToArray(), ct).AsTask(), 1024, CancellationToken.None);
        var source = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-1), "Original",
            attachmentPath: storage.Saved.Single());
        var failure = new FakeMailTransport { SendFailure = new MailConnectionException(MailConnectionFailure.Authentication, "bad auth") };
        await using (var first = BuildProvider(db, failure, storage))
            await ScheduledSendDispatcher.ProcessDueAsync(first, CancellationToken.None);
        var service = new ScheduledSendService(db, storage, FixedRuntimeSettingsStore.Operation(),
            new AuditLogger(db), NullLogger<ScheduledSendService>.Instance);
        var request = new RescheduleFailedSend(DateTime.UtcNow.AddHours(1),
            ["new@example.test"], [], [], "Edited subject", null, "new body", null);
        var rescheduled = await service.RescheduleFailedAsync(accountId, source, request,
            "user-confirmed-retry", null, CancellationToken.None);
        var retry = await db.ScheduledSends.SingleAsync(x => x.Id == rescheduled.Id);
        retry.SendAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var success = new FakeMailTransport();
        await using var second = BuildProvider(db, success, storage);

        await ScheduledSendDispatcher.ProcessDueAsync(second, CancellationToken.None);

        Assert.Equal(1, success.SentCount);
        Assert.Equal("Edited subject", success.Message!.Subject);
        Assert.Equal("new body", success.Message.TextBody);
        Assert.Equal(ScheduledSendStatus.Sent, (await db.ScheduledSends.SingleAsync(x => x.Id == rescheduled.Id)).Status);
        Assert.Equal(ScheduledSendStatus.Failed, (await db.ScheduledSends.SingleAsync(x => x.Id == source)).Status);
        Assert.Equal(2, await db.SendOperations.CountAsync());
    }

    [Fact]
    public async Task ProcessDueAsync_TwoDispatchers_ClaimOneDelivery()
    {
        var database = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database, root).Options;
        await using var seed = new AppDbContext(options);
        var accountId = await SeedAccountAsync(seed);
        var due = await SeedScheduledSendAsync(seed, accountId, DateTime.UtcNow.AddMinutes(-1), "Once");
        await using var first = new AppDbContext(options);
        await using var second = new AppDbContext(options);
        var transport = new FakeMailTransport();
        await using var firstProvider = BuildProvider(first, transport, new FakeFileStorage());
        await using var secondProvider = BuildProvider(second, transport, new FakeFileStorage());

        await Task.WhenAll(
            ScheduledSendDispatcher.ProcessDueAsync(firstProvider, CancellationToken.None),
            ScheduledSendDispatcher.ProcessDueAsync(secondProvider, CancellationToken.None));

        Assert.Equal(1, transport.SentCount);
        Assert.Equal(ScheduledSendStatus.Sent, (await seed.ScheduledSends.SingleAsync(x => x.Id == due)).Status);
    }

    [Fact]
    public async Task ProcessDueAsync_DeletesStagedAttachmentsAfterDispatch()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var storage = new FakeFileStorage();
        await storage.SaveAsync(accountId, Guid.NewGuid(), Guid.NewGuid(), (stream, ct) => stream.WriteAsync("hi"u8.ToArray(), ct).AsTask(), 1024, CancellationToken.None);
        var stagedRelativePath = storage.Saved.Single();
        var due = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-1), "With attachment", attachmentPath: stagedRelativePath);
        var transport = new FakeMailTransport();
        await using var provider = BuildProvider(db, transport, storage);

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        Assert.Contains(stagedRelativePath, storage.Deleted);
        var row = await db.ScheduledSends.AsNoTracking().SingleAsync(x => x.Id == due);
        Assert.Equal(ScheduledSendStatus.Sent, row.Status);
    }

    [Fact]
    public async Task ProcessDueAsync_SkipsCancelledRow_EvenIfDue()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var cancelled = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-5), "Cancelled", status: ScheduledSendStatus.Cancelled);
        var transport = new FakeMailTransport();
        await using var provider = BuildProvider(db, transport, new FakeFileStorage());

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        Assert.Equal(0, transport.SentCount);
        var row = await db.ScheduledSends.AsNoTracking().SingleAsync(x => x.Id == cancelled);
        Assert.Equal(ScheduledSendStatus.Cancelled, row.Status);
    }

    private static ServiceProvider BuildProvider(AppDbContext db, MailClient.Infrastructure.Mail.IMailTransport transport, FakeFileStorage storage)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IFileStorage>(storage);
        services.AddSingleton<MailClient.Infrastructure.Mail.IMailTransport>(transport);
        services.AddSingleton(new SendOperationStore(db, NullLogger<SendOperationStore>.Instance));
        services.AddSingleton(FixedRuntimeSettingsStore.Operation());
        services.AddSingleton(TestServices.InlineSync());
        services.AddSingleton(new AuditLogger(db));
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<MailSendService>>(NullLogger<MailSendService>.Instance);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ScheduledSendDispatcher>>(NullLogger<ScheduledSendDispatcher>.Instance);
        services.AddSingleton<ISyncLockProvider>(new InMemorySyncLockProvider());
        services.AddSingleton<MailSendService>();
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedScheduledSendAsync(
        AppDbContext db, Guid accountId, DateTime sendAtUtc, string subject,
        ScheduledSendStatus status = ScheduledSendStatus.Pending, string? attachmentPath = null)
    {
        var id = Guid.NewGuid();
        var entity = new ScheduledSend
        {
            Id = id,
            MailAccountId = accountId,
            ToAddressesJson = JsonSerializer.Serialize(new[] { "friend@example.test" }),
            CcAddressesJson = "[]",
            BccAddressesJson = "[]",
            Subject = subject,
            BodyText = "body",
            SendAtUtc = sendAtUtc,
            Status = status,
            CreatedAtUtc = DateTime.UtcNow,
            IdempotencyKey = $"disp-{id:N}",
            Fingerprint = "fingerprint"
        };
        if (attachmentPath is not null)
            entity.Attachments = [new ScheduledSendAttachment
            {
                Id = Guid.NewGuid(),
                ScheduledSendId = id,
                FileName = "a.txt",
                ContentType = "text/plain",
                StoragePath = attachmentPath,
                SizeBytes = 2
            }];
        db.ScheduledSends.Add(entity);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return id;
    }

    private static async Task<Guid> SeedAccountAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
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
            Status = MailAccountStatus.Active
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return accountId;
    }

    private static SendMailAttachment File(string name, string content) =>
        new(name, "text/plain", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));

    private sealed class HookedFileStorage(FakeFileStorage inner, Func<Task> beforeFirstSave) : IFileStorage
    {
        private bool _called;

        public async Task<StoredFile> SaveAsync(Guid accountId, Guid mailId, Guid attachmentId,
            Func<Stream, CancellationToken, Task> write, long maxBytes, CancellationToken cancellationToken)
        {
            if (!_called)
            {
                _called = true;
                await beforeFirstSave();
            }
            return await inner.SaveAsync(accountId, mailId, attachmentId, write, maxBytes, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(relativePath, cancellationToken);

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken) =>
            inner.DeleteAsync(relativePath, cancellationToken);

        public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
            inner.DeleteAccountAsync(accountId, cancellationToken);

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            inner.IsAvailableAsync(cancellationToken);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class OnceUnavailableTransport : MailClient.Infrastructure.Mail.IMailTransport
    {
        public int Attempts { get; private set; }
        public MimeKit.MimeMessage? Message { get; private set; }

        public Task SendAsync(MailAccount account, MimeKit.MimeMessage message, CancellationToken cancellationToken, bool requestDeliveryReceipt = false)
        {
            Attempts++;
            Message = message;
            if (Attempts == 1)
                throw new MailConnectionException(MailConnectionFailure.Network, "temporary");
            return Task.CompletedTask;
        }

        public Task AppendToSentAsync(MailAccount account, string sentFullName, MimeKit.MimeMessage message,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
