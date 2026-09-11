using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Push;

// Production IFirebaseMessageSender over the official Firebase Admin .NET SDK.
// Recipients chunk at the FCM multicast limit (500); each chunk maps to one
// SendEachForMulticastAsync call so per-recipient outcomes stay isolated.
// Errors are reported per token via MessagingErrorCode; whole-batch transport
// failures propagate so the gateway can mark every recipient failed (kept).
public sealed class FirebaseMessageSender(ILogger<FirebaseMessageSender> logger) : IFirebaseMessageSender
{
    public const int MaxTokensPerBatch = 500;

    public async Task<IReadOnlyList<FirebaseDeliveryOutcome>> SendBatchAsync(
        IReadOnlyList<FirebaseOutgoingMessage> messages,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<FirebaseDeliveryOutcome>(messages.Count);
        foreach (var chunk in messages.Chunk(MaxTokensPerBatch))
            outcomes.AddRange(await SendChunkAsync(chunk, cancellationToken));

        return outcomes;
    }

    private async Task<IReadOnlyList<FirebaseDeliveryOutcome>> SendChunkAsync(
        FirebaseOutgoingMessage[] chunk,
        CancellationToken cancellationToken)
    {
        var messaging = FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(FirebaseSetup.AppName));
        // Message.Token is the FCM registration-token field our device tokens
        // carry; the SDK marks it obsolete in favor of FID, which is a
        // different identifier and would misroute these tokens.
#pragma warning disable CS0618
        var outgoing = chunk.Select(message => new Message
        {
            Token = message.PushToken,
            Notification = new Notification
            {
                Title = message.Title,
                Body = message.Body
            },
            Data = new Dictionary<string, string>(message.Data)
        }).ToList();
#pragma warning restore CS0618

        BatchResponse response;
        try
        {
            response = await messaging.SendEachAsync(outgoing, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FCM multicast send failed for {Count} recipients.", chunk.Length);
            throw;
        }

        return chunk.Select((message, index) =>
        {
            var send = response.Responses[index];
            return send.IsSuccess
                ? new FirebaseDeliveryOutcome(message.DbId, true, null)
                : new FirebaseDeliveryOutcome(message.DbId, false, send.Exception?.MessagingErrorCode);
        }).ToList();
    }
}
