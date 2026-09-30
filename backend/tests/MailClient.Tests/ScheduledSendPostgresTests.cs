using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace MailClient.Tests;

[Collection("postgres")]
public sealed class ScheduledSendPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ProcessDueAsync_TrackedPendingRow_RetriesAndPersistsSentAfterRelationalClaim()
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        await using var db = fixture.CreateDb();
        var account = await SeedAccountAsync(db);
        var storage = new FakeFileStorage();
        var service = new ScheduledSendService(db, storage, FixedRuntimeSettingsStore.Operation(),
            new AuditLogger(db), NullLogger<ScheduledSendService>.Instance);
        var command = new SendMailCommand(account.Id, "recipient@example.test", "Tracked retry", null, "body", [])
        { IdempotencyKey = $"scheduled-{Guid.NewGuid():N}" };
        var created = await service.CreateAsync(account.Id, command, DateTime.UtcNow.AddHours(1), null, CancellationToken.None);
        var row = await db.ScheduledSends.SingleAsync(x => x.Id == created.Id);
        row.SendAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var transport = new OnceUnavailableTransport();
        await using var provider = BuildProvider(db, transport, storage);

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        var pending = await db.ScheduledSends.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(ScheduledSendStatus.Pending, pending.Status);
        Assert.Equal(1, pending.AttemptCount);
        Assert.True(pending.NextAttemptAtUtc > DateTime.UtcNow);
        row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);
        await ScheduledSendDispatcher.ProcessDueAsync(provider, CancellationToken.None);

        var sent = await db.ScheduledSends.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(ScheduledSendStatus.Sent, sent.Status);
        Assert.Equal(2, sent.AttemptCount);
        Assert.Null(sent.NextAttemptAtUtc);
        Assert.Equal(2, transport.Attempts);
        Assert.Equal(1, transport.Deliveries);
        Assert.Equal(SendOperationStatus.Sent,
            (await db.SendOperations.AsNoTracking().SingleAsync(x => x.MailAccountId == account.Id)).Status);
    }

    [Theory]
    [InlineData(SendOperationStatus.Sent)]
    [InlineData(SendOperationStatus.DeliveryUnknown)]
    public async Task ClaimAsync_StaleFailedAttempt_DoesNotResendCompletedOrUncertainDelivery(SendOperationStatus outcome)
    {
        if (!IntegrationEnvironment.PostgresEnabled) return;
        await using var first = fixture.CreateDb();
        var account = await SeedAccountAsync(first);
        var key = $"stale-retry-{Guid.NewGuid():N}";
        var fingerprint = SendOperationStore.Fingerprint(account.Id, "recipient@example.test", "Hi", null, "body", []);
        var firstStore = new SendOperationStore(first, NullLogger<SendOperationStore>.Instance);
        var original = Assert.IsType<SendOperationStore.Proceed>(
            await firstStore.ClaimAsync(account.Id, key, fingerprint, CancellationToken.None));
        await firstStore.TryFailAsync(original.Operation.Id, "Temporarily unavailable", CancellationToken.None);
        await using var second = fixture.CreateDb();
        var secondStore = new SendOperationStore(second, NullLogger<SendOperationStore>.Instance);
        var retry = Assert.IsType<SendOperationStore.Proceed>(
            await secondStore.ClaimAsync(account.Id, key, fingerprint, CancellationToken.None));
        await secondStore.TryCompleteAsync(retry.Operation.Id, outcome, false, null, CancellationToken.None);

        var claim = await firstStore.ClaimAsync(account.Id, key, fingerprint, CancellationToken.None);

        if (outcome == SendOperationStatus.Sent)
            Assert.IsType<SendOperationStore.Replay>(claim);
        else
            Assert.Equal("delivery_unknown", Assert.IsType<SendOperationStore.Denied>(claim).Code);
        Assert.Equal(outcome, (await second.SendOperations.AsNoTracking().SingleAsync(x => x.Id == original.Operation.Id)).Status);
    }

    private static ServiceProvider BuildProvider(AppDbContext db, MailClient.Infrastructure.Mail.IMailTransport transport, IFileStorage storage)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(storage);
        services.AddSingleton(transport);
        services.AddSingleton(new SendOperationStore(db, NullLogger<SendOperationStore>.Instance));
        services.AddSingleton(FixedRuntimeSettingsStore.Operation());
        services.AddSingleton(TestServices.InlineSync());
        services.AddSingleton(new AuditLogger(db));
        services.AddSingleton<ILogger<MailSendService>>(NullLogger<MailSendService>.Instance);
        services.AddSingleton<ILogger<ScheduledSendDispatcher>>(NullLogger<ScheduledSendDispatcher>.Instance);
        services.AddSingleton<ISyncLockProvider>(new PostgresSyncLockProvider(
            db.Database.GetConnectionString()!, NullLogger<PostgresSyncLockProvider>.Instance));
        services.AddSingleton<MailSendService>();
        return services.BuildServiceProvider();
    }

    private static async Task<MailAccount> SeedAccountAsync(AppDbContext db)
    {
        var email = $"scheduled-{Guid.NewGuid():N}@example.test";
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            EmailAddress = email,
            NormalizedEmailAddress = MailAccount.NormalizeEmailAddress(email),
            Username = email,
            Status = MailAccountStatus.Active,
            SaveSentCopy = false
        };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private sealed class OnceUnavailableTransport : MailClient.Infrastructure.Mail.IMailTransport
    {
        public int Attempts { get; private set; }
        public int Deliveries { get; private set; }

        public Task SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
                throw new MailConnectionException(MailConnectionFailure.Network, "temporarily unavailable");
            Deliveries++;
            return Task.CompletedTask;
        }

        public Task AppendToSentAsync(MailAccount account, string sentFullName, MimeMessage message,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
