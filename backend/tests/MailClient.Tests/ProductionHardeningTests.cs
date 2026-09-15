using System.Text;
using System.Xml;
using MailClient.Application.Discovery;
using MailClient.Infrastructure.Discovery;

namespace MailClient.Tests;

public sealed class ProductionHardeningTests
{
    [Fact]
    public void MailDiscoveryOptions_InvalidValues_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => new MailDiscoveryOptions { OverallTimeoutSeconds = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new MailDiscoveryOptions { StrategyTimeoutSeconds = 60, OverallTimeoutSeconds = 5 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new MailDiscoveryOptions { MaxDocumentBytes = 0 }.Validate());
    }

    [Fact]
    public async Task ReadBoundedAsync_BelowLimit_ReturnsAllBytes()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        using var source = new MemoryStream(payload);

        var result = await DocumentLimits.ReadBoundedAsync(source, maxBytes: 8, CancellationToken.None);

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task ReadBoundedAsync_AboveLimit_Throws()
    {
        using var source = new MemoryStream(new byte[100]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DocumentLimits.ReadBoundedAsync(source, maxBytes: 10, CancellationToken.None));
    }

    [Fact]
    public async Task Autoconfig_DtdDocument_IsRejected()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE clientConfig [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <clientConfig><emailProvider/></clientConfig>
            """;

        var http = StubHttp(_ => Ok(xml, "application/xml"));
        var strategy = new AutoconfigDiscoveryStrategy(http, Microsoft.Extensions.Options.Options.Create(new MailDiscoveryOptions()));

        await Assert.ThrowsAnyAsync<XmlException>(() => CollectAsync(strategy, "person@example.test"));
    }

    private static async Task<List<MailServerCandidate>> CollectAsync(IMailDiscoveryStrategy strategy, string email)
    {
        var candidates = new List<MailServerCandidate>();
        await foreach (var candidate in strategy.DiscoverAsync(email, CancellationToken.None))
            candidates.Add(candidate);
        return candidates;
    }

    private static HttpClient StubHttp(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> respond) =>
        new(new StubHandler(respond));

    private static System.Net.Http.HttpResponseMessage Ok(string body, string contentType) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, contentType)
    };

    private sealed class StubHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> respond) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
