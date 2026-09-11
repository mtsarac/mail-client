using FirebaseAdmin.Messaging;
using MailClient.Infrastructure.Push;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class FirebaseAdminGatewayTests
{
    [Fact]
    public async Task Success_MarksSucceededWithoutRemoval()
    {
        var gateway = CreateGateway((messages, _) =>
            messages.Select(message => new FirebaseDeliveryOutcome(message.DbId, true, null)).ToList());

        var results = await gateway.SendNewMailAsync(
            [new FirebaseRecipient(Guid.NewGuid(), "token-a")], "S", "T", Data(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Succeeded);
        Assert.False(result.RemoveToken);
    }

    [Fact]
    public async Task Unregistered_MarksForRemoval()
    {
        var gateway = CreateGateway((messages, _) =>
            messages.Select(message => new FirebaseDeliveryOutcome(message.DbId, false, MessagingErrorCode.Unregistered)).ToList());

        var results = await gateway.SendNewMailAsync(
            [new FirebaseRecipient(Guid.NewGuid(), "token-bad")], "S", "T", Data(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.True(result.RemoveToken);
    }

    [Theory]
    [InlineData(MessagingErrorCode.QuotaExceeded)]
    [InlineData(MessagingErrorCode.Unavailable)]
    [InlineData(MessagingErrorCode.Internal)]
    [InlineData(MessagingErrorCode.InvalidArgument)]
    [InlineData(MessagingErrorCode.SenderIdMismatch)]
    [InlineData(MessagingErrorCode.ThirdPartyAuthError)]
    public async Task NonUnregisteredFailures_KeepToken(MessagingErrorCode code)
    {
        var gateway = CreateGateway((messages, _) =>
            messages.Select(message => new FirebaseDeliveryOutcome(message.DbId, false, code)).ToList());

        var results = await gateway.SendNewMailAsync(
            [new FirebaseRecipient(Guid.NewGuid(), "token-x")], "S", "T", Data(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.False(result.RemoveToken);
    }

    [Fact]
    public async Task NullErrorCode_KeepsToken()
    {
        var gateway = CreateGateway((messages, _) =>
            messages.Select(message => new FirebaseDeliveryOutcome(message.DbId, false, null)).ToList());

        var result = Assert.Single(await gateway.SendNewMailAsync(
            [new FirebaseRecipient(Guid.NewGuid(), "token-x")], "S", "T", Data(), CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.False(result.RemoveToken);
    }

    [Fact]
    public async Task PartialFailure_PreservesOrderingAndIsolates()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var gateway = CreateGateway((messages, _) => messages.Select(message => message.PushToken switch
        {
            "token-ok" => new FirebaseDeliveryOutcome(message.DbId, true, null),
            "token-gone" => new FirebaseDeliveryOutcome(message.DbId, false, MessagingErrorCode.Unregistered),
            _ => new FirebaseDeliveryOutcome(message.DbId, false, MessagingErrorCode.Unavailable)
        }).ToList());

        var results = await gateway.SendNewMailAsync(
        [
            new FirebaseRecipient(first, "token-ok"),
            new FirebaseRecipient(second, "token-gone"),
            new FirebaseRecipient(third, "token-flaky")
        ], "S", "T", Data(), CancellationToken.None);

        Assert.Equal([first, second, third], results.Select(r => r.DbId).ToArray());
        Assert.True(results[0].Succeeded);
        Assert.True(results[1].RemoveToken);
        Assert.False(results[2].Succeeded);
        Assert.False(results[2].RemoveToken);
    }

    [Fact]
    public async Task ManyRecipients_AllAttempted()
    {
        var seen = new List<FirebaseOutgoingMessage>();
        var gateway = CreateGateway((messages, _) =>
        {
            seen.AddRange(messages);
            return messages.Select(message => new FirebaseDeliveryOutcome(message.DbId, true, null)).ToList();
        });
        var count = 2 * FirebaseMessageSender.MaxTokensPerBatch + 7;

        var results = await gateway.SendNewMailAsync(
            Enumerable.Range(0, count).Select(_ => new FirebaseRecipient(Guid.NewGuid(), "token")).ToList(),
            "S", "T", Data(), CancellationToken.None);

        Assert.Equal(count, results.Count);
        Assert.All(results, result => Assert.True(result.Succeeded));
    }

    [Fact]
    public async Task SenderThrows_MarksAllFailedWithoutRemovalOrThrow()
    {
        var gateway = CreateGateway((_, _) => throw new InvalidOperationException("transport down"));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var results = await gateway.SendNewMailAsync(
            ids.Select(id => new FirebaseRecipient(id, "token")).ToList(),
            "S", "T", Data(), CancellationToken.None);

        Assert.Equal(ids, results.Select(r => r.DbId).ToArray());
        Assert.All(results, result =>
        {
            Assert.False(result.Succeeded);
            Assert.False(result.RemoveToken);
        });
    }

    private static FirebaseAdminGateway CreateGateway(
        Func<IReadOnlyList<FirebaseOutgoingMessage>, CancellationToken, IReadOnlyList<FirebaseDeliveryOutcome>> send) =>
        new(new StubSender(send), NullLogger<FirebaseAdminGateway>.Instance);

    private static IReadOnlyDictionary<string, string> Data() => new Dictionary<string, string>
    {
        ["type"] = "new_mail",
        ["mailId"] = Guid.NewGuid().ToString()
    };

    private sealed class StubSender(
        Func<IReadOnlyList<FirebaseOutgoingMessage>, CancellationToken, IReadOnlyList<FirebaseDeliveryOutcome>> send)
        : IFirebaseMessageSender
    {
        public Task<IReadOnlyList<FirebaseDeliveryOutcome>> SendBatchAsync(
            IReadOnlyList<FirebaseOutgoingMessage> messages,
            CancellationToken cancellationToken) =>
            Task.FromResult(send(messages, cancellationToken));
    }
}
