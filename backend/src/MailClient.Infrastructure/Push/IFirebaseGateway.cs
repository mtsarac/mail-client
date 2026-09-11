namespace MailClient.Infrastructure.Push;

// Minimal gateway seam between the push service and the FCM transport.
// Kept small so unit tests can fake it and the FirebaseAdmin-based
// transport can evolve without touching callers.
public sealed record FirebaseRecipient(Guid DbId, string PushToken);

public sealed record FirebaseSendResult(Guid DbId, bool Succeeded, bool RemoveToken);

public interface IFirebaseGateway
{
    Task<IReadOnlyList<FirebaseSendResult>> SendNewMailAsync(
        IReadOnlyList<FirebaseRecipient> recipients,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken);
}
