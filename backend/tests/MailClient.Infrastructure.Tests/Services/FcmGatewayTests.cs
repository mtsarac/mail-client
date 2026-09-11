using System.Net;
using System.Text;
using System.Text.Json;
using MailClient.Infrastructure.Push;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class FcmGatewayTests
{
    private const string NotFoundBody = "{\"error\":{\"code\":404,\"status\":\"NOT_FOUND\"}}";
    private const string QuotaBody = "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\"}}";
    private const string AuthBody = "{\"error\":{\"code\":401,\"status\":\"UNAUTHENTICATED\"}}";
    private const string UnavailableBody = "{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}";

    [Fact]
    public async Task Success_MarksSucceededWithoutRemoval()
    {
        var handler = new QueueHandler([(HttpStatusCode.OK, "{}")]);
        var gateway = CreateGateway(handler);

        var results = await gateway.SendNewMailAsync(
            [new FirebaseRecipient(Guid.NewGuid(), "token-a")], "S", "T", Data(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Succeeded);
        Assert.False(result.RemoveToken);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unregistered_MarksForRemoval()
    {
        var handler = new QueueHandler([(HttpStatusCode.NotFound, NotFoundBody)]);
        var gateway = CreateGateway(handler);

        var results = await gateway.SendNewMailAsync(
            [new FirebaseRecipient(Guid.NewGuid(), "token-bad")], "S", "T", Data(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.True(result.RemoveToken);
    }

    [Fact]
    public async Task TransientFailures_KeepToken()
    {
        var handler = new QueueHandler(
        [
            (HttpStatusCode.TooManyRequests, QuotaBody),
            (HttpStatusCode.InternalServerError, "not-json{{{"),
            (HttpStatusCode.Unauthorized, AuthBody)
        ]);
        var gateway = CreateGateway(handler);
        var recipients = new List<FirebaseRecipient>
        {
            new(Guid.NewGuid(), "token-quota"),
            new(Guid.NewGuid(), "token-broken-body"),
            new(Guid.NewGuid(), "token-auth")
        };

        var results = await gateway.SendNewMailAsync(recipients, "S", "T", Data(), CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, result =>
        {
            Assert.False(result.Succeeded);
            Assert.False(result.RemoveToken);
        });
    }

    [Fact]
    public async Task PartialSuccess_PreservesTokenOrdering()
    {
        var handler = new QueueHandler(
        [
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.NotFound, NotFoundBody),
            (HttpStatusCode.ServiceUnavailable, UnavailableBody)
        ]);
        var gateway = CreateGateway(handler);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        var results = await gateway.SendNewMailAsync(
        [
            new FirebaseRecipient(first, "token-a"),
            new FirebaseRecipient(second, "token-b"),
            new FirebaseRecipient(third, "token-c")
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
        var count = 2 * FcmHttpGateway.MaxTokensPerBatch + 7;
        var handler = new QueueHandler(Enumerable.Repeat((HttpStatusCode.OK, "{}"), count).ToList());
        var gateway = CreateGateway(handler);

        var results = await gateway.SendNewMailAsync(
            Enumerable.Range(0, count).Select(_ => new FirebaseRecipient(Guid.NewGuid(), "token")).ToList(),
            "S", "T", Data(), CancellationToken.None);

        Assert.Equal(count, results.Count);
        Assert.Equal(count, handler.Requests.Count);
        Assert.All(results, result => Assert.True(result.Succeeded));
    }

    private static FcmHttpGateway CreateGateway(QueueHandler handler) =>
        new(new FirebaseOptions { Enabled = true, ProjectId = "demo", CredentialsPath = "" },
            NullLogger<FcmHttpGateway>.Instance,
            new StubTokenProvider(),
            new HttpClient(handler));

    private static IReadOnlyDictionary<string, string> Data() => new Dictionary<string, string>
    {
        ["type"] = "new_mail",
        ["mailId"] = Guid.NewGuid().ToString()
    };

    private sealed class StubTokenProvider : IFirebaseAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("stub-access-token");
    }

    private sealed class QueueHandler(IReadOnlyList<(HttpStatusCode Status, string Body)> responses) : HttpMessageHandler
    {
        private int _index;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = responses[Math.Min(_index++, responses.Count - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
