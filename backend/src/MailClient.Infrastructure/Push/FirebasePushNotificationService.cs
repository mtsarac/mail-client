using MailClient.Application.Interfaces;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Push;

// Post-commit, best-effort new-mail fan-out. Token loading and invalid-token
// cleanup run in a dedicated scope so a push failure can never disturb the
// already-committed sync state on the caller's DbContext.
public sealed class FirebasePushNotificationService(
    IServiceScopeFactory scopes,
    IFirebaseGateway gateway,
    ILogger<FirebasePushNotificationService> logger) : IPushNotificationService
{
    public async Task NotifyNewMailAsync(NewMailNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            await NotifyCoreAsync(notification, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Push notification failed for user {UserId}, mail {MailId}. Sync state is unaffected.",
                notification.UserId, notification.MailId);
        }
    }

    private async Task NotifyCoreAsync(NewMailNotification notification, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await db.DeviceTokens
            .AsNoTracking()
            .Where(token => token.UserId == notification.UserId)
            .Select(token => new { token.Id, token.Token })
            .ToListAsync(cancellationToken);
        if (tokens.Count == 0)
            return;

        var data = new Dictionary<string, string>
        {
            ["type"] = "new_mail",
            ["mailId"] = notification.MailId.ToString(),
            ["accountId"] = notification.AccountId.ToString(),
            ["folderId"] = notification.FolderId.ToString()
        };
        var results = await gateway.SendNewMailAsync(
            tokens.Select(token => new FirebaseRecipient(token.Id, token.Token)).ToList(),
            notification.Sender,
            notification.Subject,
            data,
            cancellationToken);

        var invalidIds = results.Where(result => result.RemoveToken).Select(result => result.DbId).ToList();
        if (invalidIds.Count > 0)
        {
            var invalid = await db.DeviceTokens
                .Where(token => invalidIds.Contains(token.Id) && token.UserId == notification.UserId)
                .ToListAsync(cancellationToken);
            db.DeviceTokens.RemoveRange(invalid);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Removed {Count} invalid device tokens for user {UserId}.", invalid.Count, notification.UserId);
        }
    }
}
