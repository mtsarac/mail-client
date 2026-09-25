namespace MailClient.Infrastructure.Push;

public sealed record FirebaseRecipient(Guid DbId, string PushToken);

public sealed record FirebaseSendResult(Guid DbId, bool Succeeded, bool RemoveToken);

public interface IFirebaseGateway
{
    Task<IReadOnlyList<FirebaseSendResult>> SendAsync(
        IReadOnlyList<FirebaseRecipient> recipients,
        string? title,
        string? body,
        IReadOnlyDictionary<string, string> data,
        bool appRendered,
        CancellationToken cancellationToken);
}
