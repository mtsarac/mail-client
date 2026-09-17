using System.Diagnostics;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Application.Runtime;
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

        var tokens = await db.DeviceTokens
            .AsNoTracking()
            .Where(token => token.MailAccountId == pushEvent.MailAccountId)
            .Select(token => new { token.Id, token.Token })
            .ToListAsync(cancellationToken);
        if (tokens.Count == 0)
            return;

        var (title, body) = NotificationText(pushEvent, push.IncludeMailPreview);
        var data = EventData(pushEvent);
        var started = Stopwatch.GetTimestamp();
        var results = await gateway.SendAsync(
            tokens.Select(token => new FirebaseRecipient(token.Id, token.Token)).ToList(),
            title,
            body,
            data,
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
        _ => false
    };

    private static (string? Title, string? Body) NotificationText(PushEvent pushEvent, bool includePreview) => pushEvent.Type switch
    {
        PushEventType.NewMail when includePreview => (
            string.IsNullOrWhiteSpace(pushEvent.SenderPreview) ? "New mail" : pushEvent.SenderPreview,
            pushEvent.SubjectPreview ?? string.Empty),
        PushEventType.NewMail => ("New mail", "You have a new message."),
        PushEventType.MailStateChanged => (null, null),
        PushEventType.AccountReauthenticationRequired => ("Mail account needs attention", "Reconnect your mail account to continue syncing."),
        PushEventType.SyncError => ("Mail sync delayed", "Mail synchronization is having trouble. Open the app for details."),
        _ => (null, null)
    };

    private static IReadOnlyDictionary<string, string> EventData(PushEvent pushEvent)
    {
        var data = new Dictionary<string, string>
        {
            ["type"] = pushEvent.Type switch
            {
                PushEventType.NewMail => "new_mail",
                PushEventType.MailStateChanged => "mail_state_changed",
                PushEventType.AccountReauthenticationRequired => "account_reauthentication_required",
                PushEventType.SyncError => "sync_error",
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
        return data;
    }
}

public sealed class NoOpPushNotificationService : IPushNotificationService
{
    public Task NotifyAsync(PushEvent pushEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}
