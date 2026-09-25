using System.Text.RegularExpressions;
using MailClient.Application.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed partial class NewMailNotifier(AppDbContext db, IPushNotificationService push, ILogger<NewMailNotifier> logger)
{
    private const int PreviewLength = 140;

    public async Task NotifyPendingAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var inboxOnly = await db.MailAccounts.AsNoTracking()
            .Where(x => x.Id == accountId)
            .Select(x => (bool?)x.NotifyInboxOnly)
            .SingleOrDefaultAsync(cancellationToken);
        if (inboxOnly is null)
            return;
        var pending = await db.Mails.Include(x => x.MailFolder)
            .Where(x => x.MailAccountId == accountId && x.NotificationPending && !x.RulePending)
            .OrderBy(x => x.ReceivedAt)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
            return;
        foreach (var mail in pending)
            mail.NotificationPending = false;
        await db.SaveChangesAsync(cancellationToken);

        foreach (var mail in pending.Where(x => !x.IsRead && x.MailFolder is not null && Notifies(x.MailFolder.FolderType, inboxOnly.Value)))
        {
            try
            {
                await push.NotifyAsync(
                    new PushEvent(
                        PushEventType.NewMail,
                        accountId,
                        mail.Id,
                        mail.ConversationId,
                        mail.MailFolderId,
                        SenderPreview: string.IsNullOrWhiteSpace(mail.FromDisplayName) ? mail.FromAddress : mail.FromDisplayName,
                        SubjectPreview: mail.Subject,
                        BodyPreview: Preview(mail.BodyText)),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Push notification failed. Sync state is unaffected.");
            }
        }
    }

    internal static bool Notifies(MailFolderType folderType, bool inboxOnly) => inboxOnly
        ? folderType == MailFolderType.Inbox
        : folderType is not (MailFolderType.Sent or MailFolderType.Drafts or MailFolderType.Trash or MailFolderType.Junk);

    internal static string Preview(string bodyText)
    {
        var collapsed = Whitespace().Replace(bodyText, " ").Trim();
        return collapsed.Length <= PreviewLength ? collapsed : collapsed[..PreviewLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
