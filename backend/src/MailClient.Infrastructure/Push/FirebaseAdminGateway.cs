using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Push;

// IFirebaseGateway over the official Firebase Admin SDK (via
// IFirebaseMessageSender). Only a definitive unregistered-token report removes
// the token; quota, auth, transient, server and malformed-request failures keep
// it. A total transport failure marks every recipient failed (kept) and never
// throws, so push can never disturb the already-committed sync state.
public sealed class FirebaseAdminGateway(
    IFirebaseMessageSender sender,
    ILogger<FirebaseAdminGateway> logger) : IFirebaseGateway
{
    public async Task<IReadOnlyList<FirebaseSendResult>> SendNewMailAsync(
        IReadOnlyList<FirebaseRecipient> recipients,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FirebaseDeliveryOutcome> outcomes;
        try
        {
            outcomes = await sender.SendBatchAsync(
                recipients.Select(recipient =>
                    new FirebaseOutgoingMessage(recipient.DbId, recipient.PushToken, title, body, data)).ToList(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FCM send failed for {Count} recipients; tokens kept.", recipients.Count);
            return recipients.Select(recipient => new FirebaseSendResult(recipient.DbId, false, false)).ToList();
        }

        return outcomes.Select(outcome => new FirebaseSendResult(
            outcome.DbId,
            outcome.Succeeded,
            !outcome.Succeeded && outcome.ErrorCode == MessagingErrorCode.Unregistered)).ToList();
    }
}
