using FirebaseAdmin.Messaging;

namespace MailClient.Infrastructure.Push;

public sealed record FirebaseOutgoingMessage(
    Guid DbId,
    string PushToken,
    string? Title,
    string? Body,
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
