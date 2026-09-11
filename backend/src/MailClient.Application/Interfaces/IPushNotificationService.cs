namespace MailClient.Application.Interfaces;

// Technology-neutral new-mail push contract. Firebase types must not leak
// through this interface; the Infrastructure implementation maps this to
// the FCM payload.
public sealed record NewMailNotification(
    Guid UserId,
    Guid MailId,
    Guid AccountId,
    Guid FolderId,
    string Sender,
    string Subject);

public interface IPushNotificationService
{
    Task NotifyNewMailAsync(NewMailNotification notification, CancellationToken cancellationToken);
}
