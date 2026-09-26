using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Api.Endpoints;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Tests;

public sealed class ReplyReminderApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task CreateListCancel_ValidatesSentMailAndKeepsAccountsIsolated()
    {
        var accountA = await SeedAccountAsync();
        var accountB = await SeedAccountAsync();
        var clientA = ClientFor(accountA.AccountId);
        var clientB = ClientFor(accountB.AccountId);

        var past = await clientA.PostAsJsonAsync($"/api/mails/{accountA.SentMailId}/reply-reminder",
            new ReplyReminderRequest(DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Equal("reply_reminder_in_past", (await past.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("code").GetString());

        var notSent = await clientA.PostAsJsonAsync($"/api/mails/{accountA.InboxMailId}/reply-reminder",
            new ReplyReminderRequest(DateTime.UtcNow.AddDays(1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notSent.StatusCode);
        Assert.Equal("reply_reminder_requires_sent_mail", (await notSent.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await clientA.PostAsJsonAsync(
            $"/api/mails/{accountB.SentMailId}/reply-reminder", new ReplyReminderRequest(DateTime.UtcNow.AddDays(1)))).StatusCode);

        var due = DateTime.UtcNow.AddDays(3);
        Assert.Equal(HttpStatusCode.OK, (await clientA.PostAsJsonAsync(
            $"/api/mails/{accountA.SentMailId}/reply-reminder", new ReplyReminderRequest(due))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientB.PostAsJsonAsync(
            $"/api/mails/{accountB.SentMailId}/reply-reminder", new ReplyReminderRequest(DateTime.UtcNow.AddDays(7)))).StatusCode);

        var itemsA = await ReadRemindersAsync(clientA);
        var reminderA = Assert.Single(itemsA);
        Assert.Equal(accountA.SentMailId, reminderA.MailId);
        Assert.Equal(due, reminderA.DueAtUtc, TimeSpan.FromSeconds(1));
        Assert.Equal("Pending", reminderA.Status);
        Assert.Equal([accountB.SentMailId], (await ReadRemindersAsync(clientB)).Select(x => x.MailId));

        Assert.Equal(HttpStatusCode.NoContent, (await clientB.DeleteAsync($"/api/mails/{accountA.SentMailId}/reply-reminder")).StatusCode);
        Assert.Single(await ReadRemindersAsync(clientA));
        Assert.Equal(HttpStatusCode.NoContent, (await clientA.DeleteAsync($"/api/mails/{accountA.SentMailId}/reply-reminder")).StatusCode);
        Assert.Empty(await ReadRemindersAsync(clientA));
    }

    private static async Task<List<(Guid MailId, string Status, DateTime DueAtUtc)>> ReadRemindersAsync(HttpClient client)
    {
        var body = (await client.GetFromJsonAsync<JsonDocument>("/api/reply-reminders"))!.RootElement;
        return body.GetProperty("items").EnumerateArray().Select(x => (
            x.GetProperty("mailId").GetGuid(),
            x.GetProperty("status").GetString()!,
            x.GetProperty("dueAtUtc").GetDateTime())).ToList();
    }

    private HttpClient ClientFor(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private async Task<(Guid AccountId, Guid SentMailId, Guid InboxMailId)> SeedAccountAsync()
    {
        var accountId = Guid.NewGuid();
        var sentFolderId = Guid.NewGuid();
        var inboxFolderId = Guid.NewGuid();
        var sentMailId = Guid.NewGuid();
        var inboxMailId = Guid.NewGuid();
        var address = $"{accountId:N}@example.test";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = address,
            NormalizedEmailAddress = MailAccount.NormalizeEmailAddress(address),
            Username = address,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.AddRange(
            new MailFolder { Id = sentFolderId, MailAccountId = accountId, Name = "Sent", FullName = "Sent", FolderType = MailFolderType.Sent, IsAvailable = true },
            new MailFolder { Id = inboxFolderId, MailAccountId = accountId, Name = "Inbox", FullName = "Inbox", FolderType = MailFolderType.Inbox, IsAvailable = true });
        db.Mails.AddRange(
            new MailEntity
            {
                Id = sentMailId,
                MailAccountId = accountId,
                MailFolderId = sentFolderId,
                Uid = 1,
                MessageId = $"sent-{sentMailId:N}@example.test",
                FromAddress = address,
                ToAddress = "recipient@example.test",
                Subject = "Sent subject",
                SentAt = DateTime.UtcNow.AddHours(-1),
                ReceivedAt = DateTime.UtcNow.AddHours(-1),
                InternalDate = DateTime.UtcNow.AddHours(-1)
            },
            new MailEntity
            {
                Id = inboxMailId,
                MailAccountId = accountId,
                MailFolderId = inboxFolderId,
                Uid = 1,
                MessageId = $"inbox-{inboxMailId:N}@example.test",
                FromAddress = "sender@example.test",
                ToAddress = address,
                Subject = "Inbox subject",
                SentAt = DateTime.UtcNow,
                ReceivedAt = DateTime.UtcNow,
                InternalDate = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        return (accountId, sentMailId, inboxMailId);
    }
}

public sealed class ReplyReminderDeliveryTests
{
    [Fact]
    public async Task ReplyDetection_MarksRepliedAndDuePassDoesNotPush()
    {
        var dbName = Guid.NewGuid().ToString("N");
        Guid reminderId;
        await using (var db = CreateDb(dbName))
        {
            var seed = await SeedAsync(db);
            reminderId = seed.ReminderId;
            db.Mails.Add(new MailEntity
            {
                Id = Guid.NewGuid(),
                MailAccountId = seed.AccountId,
                MailFolderId = seed.InboxFolderId,
                ConversationId = seed.ConversationId,
                Uid = 2,
                MessageId = "reply@example.test",
                InReplyToMessageId = "sent@example.test",
                References = "sent@example.test",
                FromAddress = "recipient@example.test",
                ToAddress = "owner@example.test",
                Subject = "Re: Waiting",
                SentAt = DateTime.UtcNow.AddMinutes(-10),
                ReceivedAt = DateTime.UtcNow.AddMinutes(-10),
                InternalDate = DateTime.UtcNow.AddMinutes(-10)
            });
            await db.SaveChangesAsync();
            await new ReplyReminderService(db).MarkRepliedAsync(seed.AccountId, CancellationToken.None);
        }
        var push = new RecordingPush();

        await RunDueAsync(dbName, push);

        Assert.Empty(push.Notifications);
        await using var check = CreateDb(dbName);
        Assert.Equal(ReplyReminderStatus.Replied, (await check.ReplyReminders.FindAsync(reminderId))!.Status);
    }

    [Fact]
    public async Task DueUnanswered_ConcurrentPassesNotifyOnceAndStaleClaimDoesNotWin()
    {
        var dbName = Guid.NewGuid().ToString("N");
        Guid reminderId;
        DateTime dueAt;
        await using (var db = CreateDb(dbName))
        {
            var seed = await SeedAsync(db);
            reminderId = seed.ReminderId;
            dueAt = (await db.ReplyReminders.FindAsync(reminderId))!.DueAtUtc;
        }
        var push = new RecordingPush();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunDueAsync(dbName, push)));
        await using var staleDb = CreateDb(dbName);
        Assert.False(await ReplyReminderDispatcher.TransitionAsync(staleDb, reminderId, dueAt,
            ReplyReminderStatus.Notified, DateTime.UtcNow, CancellationToken.None));

        var notification = Assert.Single(push.Notifications);
        Assert.Equal(PushEventType.ReplyReminder, notification.Type);
        await using var check = CreateDb(dbName);
        var reminder = await check.ReplyReminders.FindAsync(reminderId);
        Assert.Equal(ReplyReminderStatus.Notified, reminder!.Status);
        Assert.NotNull(reminder.NotifiedAt);
    }

    private static async Task RunDueAsync(string dbName, IPushNotificationService push)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton(push);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await ReplyReminderDispatcher.ProcessDueAsync(scope.ServiceProvider, CancellationToken.None);
    }

    private static AppDbContext CreateDb(string name) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(name).Options);

    private static async Task<(Guid AccountId, Guid InboxFolderId, Guid ConversationId, Guid ReminderId)> SeedAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var sentFolderId = Guid.NewGuid();
        var inboxFolderId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "owner@example.test",
            NormalizedEmailAddress = "OWNER@EXAMPLE.TEST",
            Username = "owner@example.test",
            Status = MailAccountStatus.Active
        });
        db.MailFolders.AddRange(
            new MailFolder { Id = sentFolderId, MailAccountId = accountId, Name = "Sent", FullName = "Sent", FolderType = MailFolderType.Sent },
            new MailFolder { Id = inboxFolderId, MailAccountId = accountId, Name = "Inbox", FullName = "Inbox", FolderType = MailFolderType.Inbox });
        db.Conversations.Add(new Conversation
        {
            Id = conversationId,
            MailAccountId = accountId,
            NormalizedSubject = "waiting",
            StartedAt = DateTime.UtcNow.AddHours(-2),
            LastMessageAt = DateTime.UtcNow.AddHours(-2)
        });
        db.Mails.Add(new MailEntity
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = sentFolderId,
            ConversationId = conversationId,
            Uid = 1,
            MessageId = "sent@example.test",
            FromAddress = "owner@example.test",
            ToAddress = "recipient@example.test",
            Subject = "Waiting",
            BodyText = "Please reply",
            SentAt = DateTime.UtcNow.AddHours(-2),
            ReceivedAt = DateTime.UtcNow.AddHours(-2),
            InternalDate = DateTime.UtcNow.AddHours(-2)
        });
        db.ReplyReminders.Add(new ReplyReminder
        {
            Id = reminderId,
            MailAccountId = accountId,
            MailId = mailId,
            ConversationId = conversationId,
            DueAtUtc = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            Status = ReplyReminderStatus.Pending
        });
        await db.SaveChangesAsync();
        return (accountId, inboxFolderId, conversationId, reminderId);
    }

    private sealed class RecordingPush : IPushNotificationService
    {
        private readonly ConcurrentQueue<PushEvent> notifications = new();
        public IReadOnlyCollection<PushEvent> Notifications => notifications;
        public Task NotifyAsync(PushEvent pushEvent, CancellationToken cancellationToken)
        {
            notifications.Enqueue(pushEvent);
            return Task.CompletedTask;
        }
    }
}
