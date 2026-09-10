using MailClient.Application;
using MailClient.Application.Interfaces;
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

[Collection("postgres-sync")]
public sealed class SendIdempotencyPostgresTests(PostgresSyncFixture fixture)
{
    [Fact]
    public async Task ConcurrentSameKey_SendsViaSmtpOnce()
    {
        Guid userId;
        Guid accountId;
        await using (var seed = fixture.CreateDb())
            (userId, accountId) = await SeedAccountAsync(seed);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new GatedTransport(entered, release);
        await using var dbA = fixture.CreateDb();
        await using var dbB = fixture.CreateDb();
        var serviceA = CreateService(dbA, transport);
        var serviceB = CreateService(dbB, transport);
        var command = new SendMailCommand(accountId, "friend@example.test", "Race", null, "hello", []) with
        {
            IdempotencyKey = "race-key"
        };

        var first = serviceA.SendAsync(userId, command, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await serviceB.SendAsync(userId, command, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Conflict, second.Outcome);
        release.SetResult();
        var completed = await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(completed.Value!.Sent);
        Assert.Equal(1, transport.SentCount);
    }

    private static MailSendService CreateService(AppDbContext db, IMailTransport transport) =>
        new(db, transport, new SendOperationStore(db, NullLogger<SendOperationStore>.Instance),
            new MailSyncOptions
            {
                Enabled = true,
                PollIntervalSeconds = 30,
                FlagSyncIntervalSeconds = 120,
                MaxMessagesPerRun = 10,
                MaxAttachmentBytes = 1024,
                MaxMessageAttachmentBytes = 4096,
                MaxMessageBytes = 100000
            },
            NullLogger<MailSendService>.Instance);

    private static async Task<(Guid UserId, Guid AccountId)> SeedAccountAsync(AppDbContext db)
    {
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"race-{userId:N}@example.test",
            PasswordHash = "seed",
            DisplayName = "Race"
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
            SaveSentCopy = false,
            IsActive = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (userId, accountId);
    }

    private sealed class GatedTransport(TaskCompletionSource entered, TaskCompletionSource release) : IMailTransport
    {
        private int _sentCount;
        public int SentCount => _sentCount;

        public async Task SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sentCount);
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        public Task AppendToSentAsync(MailAccount account, string sentFullName, MimeMessage message, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
