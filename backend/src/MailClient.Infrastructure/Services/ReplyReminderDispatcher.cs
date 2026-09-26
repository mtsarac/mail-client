using MailClient.Application.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class ReplyReminderDispatcher(
    IServiceScopeFactory scopes,
    ILogger<ReplyReminderDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await ProcessDueAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reply reminder pass failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal static async Task ProcessDueAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<AppDbContext>();
        var push = provider.GetRequiredService<IPushNotificationService>();
        var logger = provider.GetRequiredService<ILogger<ReplyReminderDispatcher>>();
        var now = DateTime.UtcNow;
        var due = await db.ReplyReminders.AsNoTracking()
            .Where(x => x.Status == ReplyReminderStatus.Pending && x.DueAtUtc <= now)
            .OrderBy(x => x.DueAtUtc)
            .Take(BatchSize)
            .Select(x => new { x.Id, x.MailAccountId, x.MailId, x.ConversationId, x.DueAtUtc })
            .ToListAsync(cancellationToken);

        foreach (var reminder in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReplyReminderService.HasReplyAsync(
                    db, reminder.MailAccountId, reminder.MailId, reminder.ConversationId, cancellationToken))
            {
                await TransitionAsync(db, reminder.Id, reminder.DueAtUtc, ReplyReminderStatus.Replied, now, cancellationToken);
                continue;
            }
            if (!await TransitionAsync(db, reminder.Id, reminder.DueAtUtc, ReplyReminderStatus.Notified, now, cancellationToken))
                continue;
            if (await ReplyReminderService.HasReplyAsync(
                    db, reminder.MailAccountId, reminder.MailId, reminder.ConversationId, cancellationToken))
            {
                await MarkClaimedRepliedAsync(db, reminder.Id, cancellationToken);
                continue;
            }

            var mail = await db.Mails.AsNoTracking()
                .Where(x => x.Id == reminder.MailId && x.MailAccountId == reminder.MailAccountId)
                .Select(x => new { x.Id, x.ConversationId, x.MailFolderId, x.MailFolder!.FolderType, x.ToAddress, x.Subject, x.BodyText })
                .SingleOrDefaultAsync(cancellationToken);
            if (mail is null)
                continue;
            if (mail.FolderType is MailFolderType.Trash or MailFolderType.Junk)
            {
                await CancelNotifiedAsync(db, reminder.Id, cancellationToken);
                continue;
            }
            try
            {
                await push.NotifyAsync(
                    new PushEvent(
                        PushEventType.ReplyReminder,
                        reminder.MailAccountId,
                        mail.Id,
                        mail.ConversationId,
                        mail.MailFolderId,
                        RecipientPreview: mail.ToAddress,
                        SubjectPreview: mail.Subject,
                        BodyPreview: NewMailNotifier.Preview(mail.BodyText)),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reply reminder push failed. The reminder has already been claimed.");
            }
        }
    }

    internal static async Task<bool> TransitionAsync(
        AppDbContext db,
        Guid id,
        DateTime dueAtUtc,
        ReplyReminderStatus status,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var query = db.ReplyReminders.Where(x => x.Id == id
            && x.Status == ReplyReminderStatus.Pending && x.DueAtUtc == dueAtUtc);
        if (db.Database.IsRelational())
        {
            return await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.NotifiedAt, status == ReplyReminderStatus.Notified ? now : null), cancellationToken) == 1;
        }
        var reminder = await query.SingleOrDefaultAsync(cancellationToken);
        if (reminder is null)
            return false;
        reminder.Status = status;
        reminder.NotifiedAt = status == ReplyReminderStatus.Notified ? now : null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task MarkClaimedRepliedAsync(AppDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var reminder = await db.ReplyReminders.SingleOrDefaultAsync(
            x => x.Id == id && x.Status == ReplyReminderStatus.Notified, cancellationToken);
        if (reminder is null)
            return;
        reminder.Status = ReplyReminderStatus.Replied;
        reminder.NotifiedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task CancelNotifiedAsync(AppDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var reminder = await db.ReplyReminders.SingleOrDefaultAsync(
            x => x.Id == id && x.Status == ReplyReminderStatus.Notified, cancellationToken);
        if (reminder is null)
            return;
        reminder.Status = ReplyReminderStatus.Cancelled;
        reminder.NotifiedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
