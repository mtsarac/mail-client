using System.Diagnostics;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Application.Runtime;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Push;

public sealed class FirebasePushNotificationService(
    IServiceScopeFactory scopes,
    IFirebaseGateway gateway,
    ILogger<FirebasePushNotificationService> logger,
    MailClientMetrics? metrics = null) : IPushNotificationService
{
    public async Task NotifyAsync(PushEvent pushEvent, CancellationToken cancellationToken)
    {
        try
        {
            await NotifyCoreAsync(pushEvent, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics?.RecordPushFailure(pushEvent.Type);
            logger.LogWarning(ex, "Push notification delivery failed. Sync state is unaffected.");
        }
    }

    private async Task NotifyCoreAsync(PushEvent pushEvent, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<AppDbContext>();
        var push = (await provider.GetRequiredService<IRuntimeSettingsStore>().GetAsync(cancellationToken)).Settings.Push;
        if (!push.Enabled || !IsEventEnabled(push, pushEvent.Type))
            return;

        var appRendered = IsMailNotification(pushEvent.Type);
        var privacy = NotificationPrivacy.Private;
        if (appRendered)
        {
            var preferences = await db.MailAccounts
                .AsNoTracking()
                .Where(account => account.Id == pushEvent.MailAccountId)
                .Select(account => new { account.NotificationsEnabled, account.NotificationPrivacy })
                .SingleOrDefaultAsync(cancellationToken);
            if (preferences is null || !preferences.NotificationsEnabled)
                return;
            privacy = push.IncludeMailPreview ? preferences.NotificationPrivacy : NotificationPrivacy.Private;
        }

        var tokens = await db.DeviceTokens
            .AsNoTracking()
            .Where(token => token.MailAccountId == pushEvent.MailAccountId)
            .Select(token => new { token.Id, token.Token })
            .ToListAsync(cancellationToken);
        if (tokens.Count == 0)
            return;

        var (title, body) = NotificationText(pushEvent, privacy);
        var data = EventData(pushEvent, privacy);
        var started = Stopwatch.GetTimestamp();
        var results = await gateway.SendAsync(
            tokens.Select(token => new FirebaseRecipient(token.Id, token.Token)).ToList(),
            title,
            body,
            data,
            appRendered,
            cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);

        var invalidIds = results.Where(result => result.RemoveToken).Select(result => result.DbId).ToList();
        var removed = 0;
        if (invalidIds.Count > 0)
        {
            var invalid = await db.DeviceTokens
                .Where(token => invalidIds.Contains(token.Id) && token.MailAccountId == pushEvent.MailAccountId)
                .ToListAsync(cancellationToken);
            db.DeviceTokens.RemoveRange(invalid);
            await db.SaveChangesAsync(cancellationToken);
            removed = invalid.Count;
            logger.LogInformation("Removed {Count} invalid device tokens.", invalid.Count);
        }

        var succeeded = results.Count(result => result.Succeeded);
        metrics?.RecordPushDelivery(pushEvent.Type, succeeded, results.Count - succeeded, removed, elapsed);
    }

    private static bool IsEventEnabled(RuntimePushSettings push, PushEventType type) => type switch
    {
        PushEventType.NewMail => push.NewMailEnabled,
        PushEventType.MailStateChanged => push.MailStateChangedEnabled,
        PushEventType.AccountReauthenticationRequired => push.ReauthenticationEnabled,
        PushEventType.SyncError => push.SyncErrorEnabled,
        PushEventType.SnoozeExpired => push.SnoozeExpiredEnabled,
        PushEventType.ReplyReminder => push.ReplyReminderEnabled,
        _ => false
    };

    private static bool IsMailNotification(PushEventType type) =>
        type is PushEventType.NewMail or PushEventType.SnoozeExpired or PushEventType.ReplyReminder;

    private static (string? Title, string? Body) NotificationText(PushEvent pushEvent, NotificationPrivacy privacy)
    {
        var sender = string.IsNullOrWhiteSpace(pushEvent.SenderPreview) ? null : pushEvent.SenderPreview;
        var recipient = string.IsNullOrWhiteSpace(pushEvent.RecipientPreview) ? null : pushEvent.RecipientPreview;
        var subject = pushEvent.SubjectPreview ?? string.Empty;
        var details = privacy == NotificationPrivacy.Full && !string.IsNullOrWhiteSpace(pushEvent.BodyPreview)
            ? $"{subject}\n{pushEvent.BodyPreview}"
            : subject;
        return pushEvent.Type switch
        {
            PushEventType.NewMail when privacy != NotificationPrivacy.Private => (sender ?? "New mail", details),
            PushEventType.NewMail => ("New mail", "You have a new message."),
            PushEventType.SnoozeExpired when privacy != NotificationPrivacy.Private => (
                "Snoozed mail is back",
                sender is null ? details : $"{sender}: {details}"),
            PushEventType.SnoozeExpired => ("Snoozed mail is back", "A snoozed message is back in your inbox."),
            PushEventType.ReplyReminder when privacy != NotificationPrivacy.Private => (
                "No reply yet",
                recipient is null ? details : $"{recipient}: {details}"),
            PushEventType.ReplyReminder => ("No reply yet", "A sent message has not received a reply."),
            PushEventType.MailStateChanged => (null, null),
            PushEventType.AccountReauthenticationRequired => ("Mail account needs attention", "Reconnect your mail account to continue syncing."),
            PushEventType.SyncError => ("Mail sync delayed", "Mail synchronization is having trouble. Open the app for details."),
            _ => (null, null)
        };
    }

    private static IReadOnlyDictionary<string, string> EventData(PushEvent pushEvent, NotificationPrivacy privacy)
    {
        var data = new Dictionary<string, string>
        {
            ["type"] = pushEvent.Type switch
            {
                PushEventType.NewMail => "new_mail",
                PushEventType.MailStateChanged => "mail_state_changed",
                PushEventType.AccountReauthenticationRequired => "account_reauthentication_required",
                PushEventType.SyncError => "sync_error",
                PushEventType.SnoozeExpired => "snooze_expired",
                PushEventType.ReplyReminder => "reply_reminder",
                _ => throw new InvalidOperationException("Unknown push event type.")
            },
            ["accountId"] = pushEvent.MailAccountId.ToString()
        };
        if (pushEvent.MailId is { } mailId)
            data["mailId"] = mailId.ToString();
        if (pushEvent.ConversationId is { } conversationId)
            data["conversationId"] = conversationId.ToString();
        if (pushEvent.FolderId is { } folderId)
            data["folderId"] = folderId.ToString();
        if (!string.IsNullOrWhiteSpace(pushEvent.Operation))
            data["operation"] = pushEvent.Operation;
        if (!IsMailNotification(pushEvent.Type))
            return data;
        data["privacy"] = privacy.ToString().ToLowerInvariant();
        if (privacy == NotificationPrivacy.Private)
            return data;
        if (!string.IsNullOrWhiteSpace(pushEvent.SenderPreview))
            data["sender"] = pushEvent.SenderPreview;
        if (!string.IsNullOrWhiteSpace(pushEvent.RecipientPreview))
            data["recipient"] = pushEvent.RecipientPreview;
        if (!string.IsNullOrWhiteSpace(pushEvent.SubjectPreview))
            data["subject"] = pushEvent.SubjectPreview;
        if (privacy == NotificationPrivacy.Full && !string.IsNullOrWhiteSpace(pushEvent.BodyPreview))
            data["preview"] = pushEvent.BodyPreview;
        return data;
    }
}

public sealed class NoOpPushNotificationService : IPushNotificationService
{
    public Task NotifyAsync(PushEvent pushEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}
