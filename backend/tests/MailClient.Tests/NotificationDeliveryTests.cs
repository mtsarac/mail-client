using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Tests;

public sealed class NotificationDeliveryTests
{
    [Fact]
    public async Task Notifier_InboxOnly_SkipsMailRulesMovedOrRead_AndWaitsForPendingRules()
    {
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDb(dbName);
        var seed = await SeedAsync(db, inboxOnly: true);
        var inbox = AddMail(db, seed, seed.Inbox, "inbox", body: "  first line\n\n  second   line  " + new string('x', 200));
        AddMail(db, seed, seed.Archive, "moved by rule");
        AddMail(db, seed, seed.Inbox, "marked read by rule", isRead: true);
        AddMail(db, seed, seed.Custom, "custom folder");
        var waiting = AddMail(db, seed, seed.Inbox, "rules pending", rulePending: true);
        await db.SaveChangesAsync();
        var push = new FakePushNotificationService();

        await new NewMailNotifier(db, push, NullLogger<NewMailNotifier>.Instance).NotifyPendingAsync(seed.AccountId, CancellationToken.None);

        var notification = Assert.Single(push.Notifications);
        Assert.Equal(inbox.Id, notification.MailId);
        Assert.Equal("inbox", notification.SubjectPreview);
        Assert.StartsWith("first line second line xxx", notification.BodyPreview);
        Assert.Equal(141, notification.BodyPreview!.Length);
        await using var check = CreateDb(dbName);
        Assert.Equal([waiting.Id], await check.Mails.Where(x => x.NotificationPending).Select(x => x.Id).ToListAsync());
    }

    [Fact]
    public async Task Notifier_AllFolders_NotifiesCustomAndArchive_ButNeverSentOrTrash()
    {
        await using var db = CreateDb(Guid.NewGuid().ToString("N"));
        var seed = await SeedAsync(db, inboxOnly: false);
        var custom = AddMail(db, seed, seed.Custom, "custom");
        var archive = AddMail(db, seed, seed.Archive, "archive");
        AddMail(db, seed, seed.Sent, "sent");
        AddMail(db, seed, seed.Trash, "trash");
        await db.SaveChangesAsync();
        var push = new FakePushNotificationService();

        await new NewMailNotifier(db, push, NullLogger<NewMailNotifier>.Instance).NotifyPendingAsync(seed.AccountId, CancellationToken.None);

        Assert.Equal(new[] { custom.Id, archive.Id }.Order(), push.Notifications.Select(x => x.MailId!.Value).Order());
    }

    [Fact]
    public async Task SnoozeWakeup_EndsDueSnoozesOnce_AndLeavesFutureOnes()
    {
        var dbName = Guid.NewGuid().ToString("N");
        Guid dueMail, futureSnooze;
        await using (var db = CreateDb(dbName))
        {
            var seed = await SeedAsync(db, inboxOnly: true);
            var due = AddMail(db, seed, seed.Inbox, "due", notificationPending: false);
            var future = AddMail(db, seed, seed.Inbox, "future", notificationPending: false);
            var trashed = AddMail(db, seed, seed.Trash, "trashed", notificationPending: false);
            dueMail = due.Id;
            futureSnooze = Guid.NewGuid();
            db.MailSnoozes.AddRange(
                new MailSnooze { Id = Guid.NewGuid(), MailAccountId = seed.AccountId, MailId = due.Id, UntilUtc = DateTime.UtcNow.AddMinutes(-1) },
                new MailSnooze { Id = futureSnooze, MailAccountId = seed.AccountId, MailId = future.Id, UntilUtc = DateTime.UtcNow.AddHours(1) },
                new MailSnooze { Id = Guid.NewGuid(), MailAccountId = seed.AccountId, MailId = trashed.Id, UntilUtc = DateTime.UtcNow.AddMinutes(-2) });
            await db.SaveChangesAsync();
        }
        var push = new FakePushNotificationService();

        await RunWakeupAsync(dbName, push);
        await RunWakeupAsync(dbName, push);

        var notification = Assert.Single(push.Notifications);
        Assert.Equal(PushEventType.SnoozeExpired, notification.Type);
        Assert.Equal(dueMail, notification.MailId);
        Assert.Equal("due", notification.SubjectPreview);
        await using var check = CreateDb(dbName);
        Assert.Equal([futureSnooze], await check.MailSnoozes.Select(x => x.Id).ToListAsync());
    }

    private static async Task RunWakeupAsync(string dbName, IPushNotificationService push)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton(push);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await SnoozeWakeupService.ProcessDueAsync(scope.ServiceProvider, CancellationToken.None);
    }

    private static AppDbContext CreateDb(string name) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(name).Options);

    private sealed record Seed(Guid AccountId, Guid Inbox, Guid Sent, Guid Archive, Guid Trash, Guid Custom);

    private static async Task<Seed> SeedAsync(AppDbContext db, bool inboxOnly)
    {
        var accountId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@example.test",
            NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
            Username = "a",
            Status = MailAccountStatus.Active,
            NotifyInboxOnly = inboxOnly
        });
        Guid Folder(MailFolderType type)
        {
            var id = Guid.NewGuid();
            db.MailFolders.Add(new MailFolder { Id = id, MailAccountId = accountId, Name = type.ToString(), FullName = type.ToString(), FolderType = type, IsAvailable = true });
            return id;
        }
        var seed = new Seed(accountId, Folder(MailFolderType.Inbox), Folder(MailFolderType.Sent), Folder(MailFolderType.Archive),
            Folder(MailFolderType.Trash), Folder(MailFolderType.Custom));
        await db.SaveChangesAsync();
        return seed;
    }

    private static MailEntity AddMail(AppDbContext db, Seed seed, Guid folderId, string subject, string body = "",
        bool isRead = false, bool rulePending = false, bool notificationPending = true)
    {
        var mail = new MailEntity
        {
            Id = Guid.NewGuid(),
            MailAccountId = seed.AccountId,
            MailFolderId = folderId,
            Subject = subject,
            BodyText = body,
            FromAddress = "sender@example.test",
            IsRead = isRead,
            RulePending = rulePending,
            NotificationPending = notificationPending,
            ReceivedAt = DateTime.UtcNow
        };
        db.Mails.Add(mail);
        return mail;
    }
}
