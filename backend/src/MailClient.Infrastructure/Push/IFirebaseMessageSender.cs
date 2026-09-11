using FirebaseAdmin.Messaging;

namespace MailClient.Infrastructure.Push;

// Narrow seam between the push gateway and the Firebase Admin SDK transport.
// Hand-rolled fakes implement this in tests; production uses
// FirebaseMessageSender. FirebaseAdmin types stay inside Infrastructure.
public sealed record FirebaseOutgoingMessage(
    Guid DbId,
    string PushToken,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data);

public sealed record FirebaseDeliveryOutcome(
    Guid DbId,
    bool Succeeded,
    MessagingErrorCode? ErrorCode);

public interface IFirebaseMessageSender
{
    Task<IReadOnlyList<FirebaseDeliveryOutcome>> SendBatchAsync(
        IReadOnlyList<FirebaseOutgoingMessage> messages,
        CancellationToken cancellationToken);
}
