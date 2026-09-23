using MailClient.Application.Mail;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class ScheduledSendServiceTests
{
    [Fact]
    public async Task CreateAsync_PastSendAtUtc_ThrowsScheduledSendInPast()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateService(db);
        var command = Command(accountId, "sched-past");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(accountId, command, DateTime.UtcNow.AddMinutes(-1), null, CancellationToken.None));

        Assert.Equal("scheduled_send_in_past", error.Message);
        Assert.Empty(await db.ScheduledSends.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_Success_PersistsPendingRowWithAuditAndStagesAttachments()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var storage = new FakeFileStorage();
        var service = CreateService(db, storage);
        var sendAtUtc = DateTime.UtcNow.AddHours(1);
        var command = Command(accountId, "sched-1", attachment: true);

        var result = await service.CreateAsync(accountId, command, sendAtUtc, "corr-1", CancellationToken.None);

        Assert.Equal(ScheduledSendStatus.Pending, result.Status);
        Assert.Equal(sendAtUtc, result.SendAtUtc);
        var row = await db.ScheduledSends.Include(x => x.Attachments).SingleAsync();
        Assert.Equal(result.Id, row.Id);
        Assert.Equal("Hello", row.Subject);
        Assert.Single(row.Attachments);
        Assert.Single(storage.Saved);
        Assert.NotNull(await db.AuditLogs.SingleOrDefaultAsync(x => x.Action == AuditActions.ScheduledSendCreated && x.MailAccountId == accountId));
    }

    [Fact]
    public async Task CreateAsync_ReplaySameIdempotencyKeyAndBody_ReturnsExistingRow()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateService(db);
        var sendAtUtc = DateTime.UtcNow.AddHours(1);

        var first = await service.CreateAsync(accountId, Command(accountId, "sched-2"), sendAtUtc, null, CancellationToken.None);
        var replay = await service.CreateAsync(accountId, Command(accountId, "sched-2"), sendAtUtc, null, CancellationToken.None);

        Assert.Equal(first.Id, replay.Id);
        Assert.Single(await db.ScheduledSends.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_SameKeyDifferentBody_ThrowsIdempotencyConflict()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateService(db);
        var sendAtUtc = DateTime.UtcNow.AddHours(1);
        await service.CreateAsync(accountId, Command(accountId, "sched-3"), sendAtUtc, null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(accountId, Command(accountId, "sched-3", subject: "Different"), sendAtUtc, null, CancellationToken.None));

        Assert.Equal("idempotency_conflict", error.Message);
    }

    [Fact]
    public async Task CancelAsync_PendingRow_TransitionsToCancelledAndDeletesStagedAttachments()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var storage = new FakeFileStorage();
        var service = CreateService(db, storage);
        var created = await service.CreateAsync(accountId, Command(accountId, "sched-4", attachment: true), DateTime.UtcNow.AddHours(1), null, CancellationToken.None);
        var stagedPath = storage.Saved.Single();

        await service.CancelAsync(accountId, created.Id, CancellationToken.None);

        var row = await db.ScheduledSends.SingleAsync();
        Assert.Equal(ScheduledSendStatus.Cancelled, row.Status);
        Assert.Empty(await db.ScheduledSendAttachments.ToListAsync());
        Assert.Contains(stagedPath, storage.Deleted);
        Assert.NotNull(await db.AuditLogs.SingleOrDefaultAsync(x => x.Action == AuditActions.ScheduledSendCancelled));
    }

    [Fact]
    public async Task CancelAsync_AlreadySent_ThrowsScheduledSendAlreadySent()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateService(db);
        var created = await service.CreateAsync(accountId, Command(accountId, "sched-5"), DateTime.UtcNow.AddHours(1), null, CancellationToken.None);
        var row = await db.ScheduledSends.SingleAsync(x => x.Id == created.Id);
        row.Status = ScheduledSendStatus.Sent;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(accountId, created.Id, CancellationToken.None));

        Assert.Equal("scheduled_send_already_sent", error.Message);
    }

    [Fact]
    public async Task CancelAsync_UnknownId_ThrowsScheduledSendNotFound()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(accountId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("scheduled_send_not_found", error.Message);
    }

    [Fact]
    public async Task ListAsync_OrdersBySendAtUtcAscending()
    {
        await using var db = CreateDb();
        var accountId = await SeedAccountAsync(db);
        var service = CreateService(db);
        var later = await service.CreateAsync(accountId, Command(accountId, "sched-later"), DateTime.UtcNow.AddHours(2), null, CancellationToken.None);
        var sooner = await service.CreateAsync(accountId, Command(accountId, "sched-sooner"), DateTime.UtcNow.AddHours(1), null, CancellationToken.None);

        var items = await service.ListAsync(accountId, CancellationToken.None);

        Assert.Equal([sooner.Id, later.Id], items.Select(x => x.Id));
    }

    private static SendMailCommand Command(Guid accountId, string idempotencyKey, string subject = "Hello", bool attachment = false) =>
        new(accountId, ["friend@example.test"], [], [], subject, null, "body",
            attachment ? [new SendMailAttachment("a.txt", "text/plain", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello")))] : [])
        {
            IdempotencyKey = idempotencyKey
        };

    private static ScheduledSendService CreateService(AppDbContext db, FakeFileStorage? storage = null) =>
        new(db, storage ?? new FakeFileStorage(), FixedRuntimeSettingsStore.Operation(), new AuditLogger(db), NullLogger<ScheduledSendService>.Instance);

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
