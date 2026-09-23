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
    public async Task ProcessDueAsync_TransportFailure_MarksRowFailedWithReason()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var due = await SeedScheduledSendAsync(db, accountId, DateTime.UtcNow.AddMinutes(-1), "Will fail");
        var transport = new FakeMailTransport { SendFailure = new MailConnectionException(MailConnectionFailure.Network, "down") };
        await using var provider = BuildProvider(db, transport, new FakeFileStorage());

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        var row = await db.ScheduledSends.AsNoTracking().SingleAsync(x => x.Id == due);
        Assert.Equal(ScheduledSendStatus.Failed, row.Status);
        Assert.False(string.IsNullOrWhiteSpace(row.FailureReason));
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

    private static ServiceProvider BuildProvider(AppDbContext db, FakeMailTransport transport, FakeFileStorage storage)
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

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
