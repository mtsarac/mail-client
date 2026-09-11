using MailClient.Application.Interfaces;

// Safe push no-op used when Firebase is disabled.
namespace MailClient.Infrastructure.Push;

// Used when Firebase:Enabled is false so local development and tests never
// touch credentials, the network, or per-call feature flags in sync logic.
public sealed class NoOpPushNotificationService : IPushNotificationService
{
    public Task NotifyNewMailAsync(NewMailNotification notification, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
