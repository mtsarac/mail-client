using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Push;

public sealed class FirebaseAdminGateway(
    IFirebaseMessageSender sender,
    ILogger<FirebaseAdminGateway> logger) : IFirebaseGateway
{
    public async Task<IReadOnlyList<FirebaseSendResult>> SendAsync(
        IReadOnlyList<FirebaseRecipient> recipients,
        string? title,
        string? body,
        IReadOnlyDictionary<string, string> data,
        bool appRendered,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FirebaseDeliveryOutcome> outcomes;
        try
        {
            outcomes = await sender.SendBatchAsync(
                recipients.Select(recipient =>
                    new FirebaseOutgoingMessage(recipient.DbId, recipient.PushToken, title, body, data, appRendered)).ToList(),
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
            !outcome.Succeeded && IsPermanent(outcome.ErrorCode))).ToList();
    }

    private static bool IsPermanent(MessagingErrorCode? errorCode) => errorCode is
        MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument;
}
