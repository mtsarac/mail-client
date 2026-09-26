using MailClient.Application.Conversations;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed record ReplyReminderItem(
    Guid Id,
    Guid MailId,
    Guid? ConversationId,
    DateTime DueAtUtc,
    DateTime CreatedAt,
    ReplyReminderStatus Status,
    DateTime? NotifiedAt,
    string Subject,
    string Recipient,
    DateTime SentAt);

public sealed class ReplyReminderService(AppDbContext db)
{
    public async Task<ReplyReminderItem> SetAsync(Guid accountId, Guid mailId, DateTime dueAtUtc, CancellationToken cancellationToken)
    {
        dueAtUtc = NormalizeUtc(dueAtUtc);
        if (dueAtUtc <= DateTime.UtcNow)
            throw new InvalidOperationException("reply_reminder_in_past");

        var mail = await db.Mails.Include(x => x.MailFolder)
            .SingleOrDefaultAsync(x => x.Id == mailId && x.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("mail_not_found");
        var normalizedAccount = await db.MailAccounts
            .Where(x => x.Id == accountId)
            .Select(x => x.NormalizedEmailAddress)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("mail_account_not_found");
        var normalizedFrom = MailAccount.NormalizeEmailAddress(mail.FromAddress);
        var sent = mail.MailFolder?.FolderType == MailFolderType.Sent
            || (mail.MailFolder?.FolderType != MailFolderType.Drafts && normalizedFrom == normalizedAccount);
        if (!sent)
            throw new InvalidOperationException("reply_reminder_requires_sent_mail");
        if (await HasReplyAsync(db, accountId, mailId, mail.ConversationId, cancellationToken))
            throw new InvalidOperationException("reply_reminder_already_replied");

        var now = DateTime.UtcNow;
        var reminder = await db.ReplyReminders
            .SingleOrDefaultAsync(x => x.MailAccountId == accountId && x.MailId == mailId, cancellationToken);
        if (reminder is null)
        {
            reminder = new ReplyReminder
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                MailId = mailId
            };
            db.ReplyReminders.Add(reminder);
        }
        reminder.ConversationId = mail.ConversationId;
        reminder.DueAtUtc = dueAtUtc;
        reminder.CreatedAt = now;
        reminder.Status = ReplyReminderStatus.Pending;
        reminder.NotifiedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return ToItem(reminder, mail);
    }

    public async Task<IReadOnlyList<ReplyReminderItem>> ListAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.ReplyReminders.AsNoTracking()
            .Where(x => x.MailAccountId == accountId && x.Status == ReplyReminderStatus.Pending)
            .OrderBy(x => x.DueAtUtc)
            .Join(db.Mails.AsNoTracking(), reminder => reminder.MailId, mail => mail.Id,
                (reminder, mail) => new ReplyReminderItem(
                    reminder.Id,
                    reminder.MailId,
                    reminder.ConversationId,
                    reminder.DueAtUtc,
                    reminder.CreatedAt,
                    reminder.Status,
                    reminder.NotifiedAt,
                    mail.Subject,
                    mail.ToAddress,
                    mail.SentAt))
            .ToListAsync(cancellationToken);

    public async Task<bool> CancelAsync(Guid accountId, Guid mailId, CancellationToken cancellationToken)
    {
        var reminder = await db.ReplyReminders
            .SingleOrDefaultAsync(x => x.MailAccountId == accountId && x.MailId == mailId, cancellationToken);
        if (reminder is null || reminder.Status is ReplyReminderStatus.Cancelled or ReplyReminderStatus.Replied)
            return false;
        reminder.Status = ReplyReminderStatus.Cancelled;
        reminder.NotifiedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task MarkRepliedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var reminders = await db.ReplyReminders.AsNoTracking()
            .Where(x => x.MailAccountId == accountId
                && (x.Status == ReplyReminderStatus.Pending || x.Status == ReplyReminderStatus.Notified))
            .Select(x => new { x.Id, x.MailId, x.ConversationId, x.Status })
            .ToListAsync(cancellationToken);
        foreach (var reminder in reminders)
        {
            if (!await HasReplyAsync(db, accountId, reminder.MailId, reminder.ConversationId, cancellationToken))
                continue;
            var row = await db.ReplyReminders.SingleOrDefaultAsync(x => x.Id == reminder.Id
                && x.MailAccountId == accountId && x.Status == reminder.Status, cancellationToken);
            if (row is null)
                continue;
            row.Status = ReplyReminderStatus.Replied;
            row.NotifiedAt = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    internal static async Task<bool> HasReplyAsync(
        AppDbContext db,
        Guid accountId,
        Guid sentMailId,
        Guid? reminderConversationId,
        CancellationToken cancellationToken)
    {
        var sent = await db.Mails.AsNoTracking()
            .Where(x => x.Id == sentMailId && x.MailAccountId == accountId)
            .Select(x => new { x.Id, x.ConversationId, x.MessageId, x.SentAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (sent is null)
            return false;
        var normalizedAccount = await db.MailAccounts.AsNoTracking()
            .Where(x => x.Id == accountId)
            .Select(x => x.NormalizedEmailAddress)
            .SingleOrDefaultAsync(cancellationToken);
        if (normalizedAccount is null)
            return false;
        var conversationId = sent.ConversationId ?? reminderConversationId;
        var messageId = ConversationEngine.NormalizeMessageId(sent.MessageId);
        var candidates = await db.Mails.AsNoTracking()
            .Where(x => x.MailAccountId == accountId
                && x.Id != sent.Id
                && x.SentAt > sent.SentAt
                && x.FromAddress.ToUpper() != normalizedAccount
                && ((conversationId != null && x.ConversationId == conversationId)
                    || (messageId != "" && (x.InReplyToMessageId == messageId || x.References.Contains(messageId)))))
            .Select(x => new { x.ConversationId, x.InReplyToMessageId, x.References })
            .ToListAsync(cancellationToken);
        return candidates.Any(x => x.ConversationId == conversationId
            || ConversationEngine.NormalizeMessageId(x.InReplyToMessageId) == messageId
            || ConversationEngine.ParseReferences(x.References).Contains(messageId, StringComparer.Ordinal));
    }

    private static ReplyReminderItem ToItem(ReplyReminder reminder, MailClient.Domain.Entities.Mail mail) => new(
        reminder.Id,
        reminder.MailId,
        reminder.ConversationId,
        reminder.DueAtUtc,
        reminder.CreatedAt,
        reminder.Status,
        reminder.NotifiedAt,
        mail.Subject,
        mail.ToAddress,
        mail.SentAt);

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
