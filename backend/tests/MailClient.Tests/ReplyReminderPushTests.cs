using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class ReplyReminderPushTests
{
    [Theory]
    [InlineData(NotificationPrivacy.Full, "recipient@example.test", "subject", "preview")]
    [InlineData(NotificationPrivacy.Limited, "recipient@example.test", "subject", null)]
    [InlineData(NotificationPrivacy.Private, null, null, null)]
    public async Task ReplyReminder_HonorsAccountPrivacy(
        NotificationPrivacy privacy,
        string? recipient,
        string? subject,
        string? preview)
    {
        var dbName = Guid.NewGuid().ToString("N");
        var accountId = await SeedAsync(dbName, privacy, enabled: true);
        var gateway = new Gateway();
        var service = CreateService(dbName, gateway);

        await service.NotifyAsync(new PushEvent(
            PushEventType.ReplyReminder,
            accountId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            RecipientPreview: "recipient@example.test",
            SubjectPreview: "subject",
            BodyPreview: "preview"), CancellationToken.None);

        var call = Assert.Single(gateway.Calls);
        Assert.Equal("reply_reminder", call.Data["type"]);
        Assert.Equal(recipient, call.Data.GetValueOrDefault("recipient"));
        Assert.Equal(subject, call.Data.GetValueOrDefault("subject"));
        Assert.Equal(preview, call.Data.GetValueOrDefault("preview"));
        Assert.True(call.AppRendered);
        if (privacy == NotificationPrivacy.Private)
            Assert.DoesNotContain("recipient@example.test", call.Title + call.Body + string.Join(' ', call.Data.Values));
    }

    [Fact]
    public async Task ReplyReminder_RespectsAccountNotificationToggle()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var accountId = await SeedAsync(dbName, NotificationPrivacy.Full, enabled: false);
        var gateway = new Gateway();
        var service = CreateService(dbName, gateway);

        await service.NotifyAsync(new PushEvent(PushEventType.ReplyReminder, accountId, Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(gateway.Calls);
    }

    private static async Task<Guid> SeedAsync(string dbName, NotificationPrivacy privacy, bool enabled)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName).Options);
        var accountId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "owner@example.test",
            NormalizedEmailAddress = "OWNER@EXAMPLE.TEST",
            Username = "owner@example.test",
            Status = MailAccountStatus.Active,
            NotificationsEnabled = enabled,
            NotificationPrivacy = privacy
        });
        db.DeviceTokens.Add(new DeviceToken
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Token = "token",
            Platform = "android",
            RegisteredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return accountId;
    }

    private static FirebasePushNotificationService CreateService(string dbName, Gateway gateway)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IRuntimeSettingsStore>(new SettingsStore());
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return new FirebasePushNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            gateway,
            NullLogger<FirebasePushNotificationService>.Instance);
    }

    private sealed class SettingsStore : IRuntimeSettingsStore
    {
        public Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeSettingsSnapshot(new RuntimeSettings(), 1, DateTime.UtcNow));

        public Task<RuntimeSettingsSnapshot> ReplaceAsync(
            int expectedVersion,
            RuntimeSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeSettingsSnapshot(settings, expectedVersion + 1, DateTime.UtcNow));
    }

    private sealed class Gateway : IFirebaseGateway
    {
        public List<(string? Title, string? Body, IReadOnlyDictionary<string, string> Data, bool AppRendered)> Calls { get; } = [];

        public Task<IReadOnlyList<FirebaseSendResult>> SendAsync(
            IReadOnlyList<FirebaseRecipient> recipients,
            string? title,
            string? body,
            IReadOnlyDictionary<string, string> data,
            bool appRendered,
            CancellationToken cancellationToken)
        {
            Calls.Add((title, body, data, appRendered));
            return Task.FromResult<IReadOnlyList<FirebaseSendResult>>(
                recipients.Select(x => new FirebaseSendResult(x.DbId, true, false)).ToList());
        }
    }
}
