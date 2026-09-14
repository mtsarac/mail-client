using System.Net;
using System.Text;
using DnsClient;
using MailClient.Application.Discovery;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Discovery;

namespace MailClient.Tests;

public sealed class DiscoveryStrategyTests
{
    [Theory]
    [InlineData("person@gmail.com", MailProvider.Google, "imap.gmail.com", "smtp.gmail.com", 465, MailSecurity.SslOnConnect)]
    [InlineData("person@googlemail.com", MailProvider.Google, "imap.gmail.com", "smtp.gmail.com", 465, MailSecurity.SslOnConnect)]
    [InlineData("person@outlook.com", MailProvider.Microsoft, "outlook.office365.com", "smtp.office365.com", 587, MailSecurity.StartTls)]
    [InlineData("person@hotmail.com", MailProvider.Microsoft, "outlook.office365.com", "smtp.office365.com", 587, MailSecurity.StartTls)]
    [InlineData("person@icloud.com", MailProvider.ICloud, "imap.mail.me.com", "smtp.mail.me.com", 465, MailSecurity.SslOnConnect)]
    [InlineData("person@yahoo.com", MailProvider.Yahoo, "imap.mail.yahoo.com", "smtp.mail.yahoo.com", 465, MailSecurity.SslOnConnect)]
    public async Task KnownProvider_ReturnsSecureCatalogCandidate(
        string email, MailProvider provider, string imapHost, string smtpHost, int smtpPort, MailSecurity smtpSecurity)
    {
        var candidate = await SingleAsync(new KnownProviderStrategy(), email);

        Assert.Equal(provider, candidate.Provider);
        Assert.Equal(imapHost, candidate.Imap.Host);
        Assert.Equal(993, candidate.Imap.Port);
        Assert.Equal(MailSecurity.SslOnConnect, candidate.Imap.Security);
        Assert.Equal(smtpHost, candidate.Smtp.Host);
        Assert.Equal(smtpPort, candidate.Smtp.Port);
        Assert.Equal(smtpSecurity, candidate.Smtp.Security);
        Assert.Equal(DiscoverySource.KnownProvider, candidate.Source);
        Assert.Equal([AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword], candidate.AuthenticationMethods);
    }

    [Fact]
    public async Task KnownProvider_UnknownDomain_ReturnsNoCandidate()
    {
        Assert.Empty(await CollectAsync(new KnownProviderStrategy(), "person@enterprise.example"));
    }

    [Fact]
    public async Task Heuristic_TriesImapThenMailHosts_WithSecurePorts()
    {
        var candidates = await CollectAsync(new HeuristicDiscoveryStrategy(), "person@example.test");

        Assert.Equal(2, candidates.Count);
        Assert.Equal("imap.example.test", candidates[0].Imap.Host);
        Assert.Equal("smtp.example.test", candidates[0].Smtp.Host);
        Assert.Equal("mail.example.test", candidates[1].Imap.Host);
        Assert.Equal("mail.example.test", candidates[1].Smtp.Host);
        Assert.All(candidates, candidate => Assert.Equal(MailSecurity.SslOnConnect, candidate.Imap.Security));
        Assert.All(candidates, candidate => Assert.Equal(DiscoverySource.Heuristic, candidate.Source));
    }

    [Fact]
    public async Task Autoconfig_ParsesThunderbirdDocument_WithSslAndStartTls()
    {
        const string xml = """
            <?xml version="1.0"?>
            <clientConfig version="1.1">
              <emailProvider id="example.test">
                <incomingServer type="imap">
                  <hostname>imap.example.test</hostname>
                  <port>993</port>
                  <socketType>SSL</socketType>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.example.test</hostname>
                  <port>587</port>
                  <socketType>STARTTLS</socketType>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        var http = StubHttp(request => request.RequestUri!.Host == "autoconfig.example.test"
            ? Ok(xml, "application/xml")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var candidate = await SingleAsync(new AutoconfigDiscoveryStrategy(http), "person@example.test");

        Assert.Equal("imap.example.test", candidate.Imap.Host);
        Assert.Equal(MailSecurity.SslOnConnect, candidate.Imap.Security);
        Assert.Equal("smtp.example.test", candidate.Smtp.Host);
        Assert.Equal(587, candidate.Smtp.Port);
        Assert.Equal(MailSecurity.StartTls, candidate.Smtp.Security);
        Assert.Equal(DiscoverySource.Autoconfig, candidate.Source);
    }

    [Fact]
    public async Task Autoconfig_PlaintextSocketType_IsRejected()
    {
        const string xml = """
            <clientConfig>
              <emailProvider>
                <incomingServer type="imap">
                  <hostname>imap.example.test</hostname>
                  <port>143</port>
                  <socketType>plain</socketType>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.example.test</hostname>
                  <port>587</port>
                  <socketType>STARTTLS</socketType>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        Assert.Empty(await CollectAsync(new AutoconfigDiscoveryStrategy(StubHttp(_ => Ok(xml, "application/xml"))), "person@example.test"));
    }

    [Fact]
    public async Task Autodiscover_HttpsUrl_ReturnsCandidate_HttpUrlIsRejected()
    {
        var secure = new MicrosoftAutodiscoverStrategy(StubHttp(_ => Ok("""{"Protocol":"IMAP","Url":"https://outlook.office365.com:993/imap"}""", "application/json")));
        var candidate = await SingleAsync(secure, "person@example.test");
        Assert.Equal(MailProvider.Microsoft, candidate.Provider);
        Assert.Equal("outlook.office365.com", candidate.Imap.Host);
        Assert.Equal(DiscoverySource.Autodiscover, candidate.Source);
        Assert.Equal([AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword], candidate.AuthenticationMethods);

        var insecure = new MicrosoftAutodiscoverStrategy(StubHttp(_ => Ok("""{"Protocol":"IMAP","Url":"http://insecure.example.test/imap"}""", "application/json")));
        Assert.Empty(await CollectAsync(insecure, "person@example.test"));
    }

    [Fact]
    public async Task DnsSrv_PrefersSecureServices_AndMapsSecurityFromPort()
    {
        var strategy = new StubSrvStrategy(new Dictionary<string, (string Host, int Port)>
        {
            ["_imaps._tcp.example.test"] = ("imap.example.test", 993),
            ["_submissions._tcp.example.test"] = ("smtp.example.test", 587)
        });

        var candidate = await SingleAsync(strategy, "person@example.test");

        Assert.Equal("imap.example.test", candidate.Imap.Host);
        Assert.Equal(MailSecurity.SslOnConnect, candidate.Imap.Security);
        Assert.Equal("smtp.example.test", candidate.Smtp.Host);
        Assert.Equal(587, candidate.Smtp.Port);
        Assert.Equal(MailSecurity.StartTls, candidate.Smtp.Security);
        Assert.Equal(DiscoverySource.DnsSrv, candidate.Source);
    }

    [Fact]
    public async Task DnsSrv_RequiresBothImapAndSmtpRecords()
    {
        var onlyImap = new StubSrvStrategy(new Dictionary<string, (string Host, int Port)>
        {
            ["_imaps._tcp.example.test"] = ("imap.example.test", 993)
        });

        Assert.Empty(await CollectAsync(onlyImap, "person@example.test"));
    }

    [Fact]
    public async Task DiscoveryService_SkipsRejectedCandidate_AndReturnsNextValidated()
    {
        List<string> visited = [];
        var strategies = new IMailDiscoveryStrategy[]
        {
            new FakeStrategy(1, visited, "rejected.example.test"),
            new FakeStrategy(2, visited, "accepted.example.test")
        };
        var service = new MailServerDiscoveryService(strategies, new AcceptedHostValidator("accepted.example.test"));

        var candidate = await service.DiscoverAsync("person@example.test", CancellationToken.None);

        Assert.Equal("accepted.example.test", candidate!.Imap.Host);
        Assert.Equal(["s1", "s2"], visited);
    }

    private static async Task<MailServerCandidate> SingleAsync(IMailDiscoveryStrategy strategy, string email)
    {
        var candidates = await CollectAsync(strategy, email);
        return Assert.Single(candidates);
    }

    private static async Task<List<MailServerCandidate>> CollectAsync(IMailDiscoveryStrategy strategy, string email)
    {
        var candidates = new List<MailServerCandidate>();
        await foreach (var candidate in strategy.DiscoverAsync(email, CancellationToken.None))
            candidates.Add(candidate);
        return candidates;
    }

    private static HttpClient StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new StubHandler(respond));

    private static HttpResponseMessage Ok(string body, string contentType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, contentType)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubSrvStrategy(Dictionary<string, (string Host, int Port)> records)
        : DnsSrvDiscoveryStrategy(new LookupClient())
    {
        protected override Task<(string Host, int Port)?> QueryAsync(string domain, string[] services, CancellationToken cancellationToken)
        {
            foreach (var service in services)
                if (records.TryGetValue($"{service}.{domain}", out var record))
                    return Task.FromResult<(string Host, int Port)?>(record);
            return Task.FromResult<(string Host, int Port)?>(null);
        }
    }

    private sealed class FakeStrategy(int order, List<string> visited, string imapHost) : IMailDiscoveryStrategy
    {
        public int Order => order;

        public async IAsyncEnumerable<MailServerCandidate> DiscoverAsync(
            string email,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            visited.Add($"s{order}");
            yield return new MailServerCandidate(
                MailProvider.Custom,
                new MailEndpoint(imapHost, 993, MailSecurity.SslOnConnect),
                new MailEndpoint("smtp.example.test", 465, MailSecurity.SslOnConnect),
                [AuthenticationMethod.Password],
                DiscoverySource.Heuristic);
            await Task.CompletedTask;
        }
    }

    private sealed class AcceptedHostValidator(string accepted) : IMailServerCandidateValidator
    {
        public Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(candidate.Imap.Host == accepted);
    }
}
