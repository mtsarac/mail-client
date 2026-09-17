using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Push;

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
#pragma warning disable CS0618
        var outgoing = chunk.Select(message => new Message
        {
            Token = message.PushToken,
            Notification = message.Title is null && message.Body is null
                ? null
                : new Notification
                {
                    Title = message.Title ?? string.Empty,
                    Body = message.Body ?? string.Empty
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
