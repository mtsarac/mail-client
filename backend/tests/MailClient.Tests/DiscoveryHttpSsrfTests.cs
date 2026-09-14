using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailClient.Infrastructure.Network;

namespace MailClient.Tests;

public sealed class DiscoveryHttpSsrfTests
{
    [Theory]
    [InlineData("https://127.0.0.1/mail/config-v1.1.xml")]
    [InlineData("https://[::1]/mail/config-v1.1.xml")]
    [InlineData("https://10.0.0.1/mail/config-v1.1.xml")]
    [InlineData("https://192.168.1.1/mail/config-v1.1.xml")]
    [InlineData("https://169.254.169.254/mail/config-v1.1.xml")]
    public async Task BlockedLiteral_IsRejectedBeforeConnect(string url)
    {
        using var handler = new SsrfSafeDiscoveryHttpHandler(new OutboundHostValidator(new FakeDns(IPAddress.Loopback)));
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url, timeout.Token));

        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public async Task BlockedHostname_IsRejectedBeforeConnect()
    {
        using var handler = new SsrfSafeDiscoveryHttpHandler(new OutboundHostValidator(new FakeDns(IPAddress.Parse("169.254.169.254"))));
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync("https://autoconfig.evil.test/mail/config-v1.1.xml", timeout.Token));

        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public async Task ControlledPublicDestination_IsAllowed()
    {
        var validator = new OutboundHostValidator(new FakeDns(IPAddress.Parse("93.184.216.34")));

        var validated = await validator.ResolveAllowedAsync("autoconfig.example.test", CancellationToken.None);

        Assert.Equal("autoconfig.example.test", validated.Host);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), validated.Address);
    }

    [Fact]
    public async Task ConnectsToValidatedIp_NotSystemDns_AndPreservesUriHost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var validator = new LoopbackPinningValidator();
        using var handler = new SsrfSafeDiscoveryHttpHandler(validator);
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(listener, timeout.Token);

        using var response = await http.GetAsync($"http://rebind.example.test:{port}/mail/config-v1.1.xml", timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal("rebind.example.test", validator.LastHost);
        var request = await server;
        Assert.Contains("Host: rebind.example.test", request);
    }

    [Fact]
    public async Task Https_PinsConnection_PreservesSniHost_AndRejectsSelfSignedCertificate()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var certificate = CreateSelfSignedCertificate("CN=autoconfig.example.test");
        string? sni = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, hostName) =>
                    {
                        sni = hostName;
                        return certificate;
                    }
                });
            }
            catch (Exception) when (sni is not null)
            {
                // Client aborts the handshake after rejecting the self-signed certificate.
            }
        });
        using var handler = new SsrfSafeDiscoveryHttpHandler(new LoopbackPinningValidator());
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync($"https://autoconfig.example.test:{port}/mail/config-v1.1.xml", timeout.Token));

        Assert.True(failure.InnerException is AuthenticationException,
            $"Expected certificate validation failure, got: {failure.InnerException}");
        Assert.Equal("autoconfig.example.test", sni);
        await server;
    }

    [Fact]
    public void Redirects_AreDisabled()
    {
        using var handler = new SsrfSafeDiscoveryHttpHandler(new OutboundHostValidator(new FakeDns(IPAddress.Loopback)));

        Assert.False(handler.AllowAutoRedirect);
    }

    private static async Task<string> ServeAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var buffer = new byte[4096];
        var read = await stream.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, cancellationToken);
        var request = Encoding.ASCII.GetString(buffer, 0, read);
        var body = "ok"u8.ToArray();
        var header = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        return request;
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName(subject), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    private sealed class LoopbackPinningValidator() : OutboundHostValidator(new FakeDns(IPAddress.Loopback))
    {
        public string? LastHost { get; private set; }

        public override Task<ValidatedHost> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
        {
            LastHost = host;
            return Task.FromResult(new ValidatedHost(host, IPAddress.Loopback));
        }
    }
}
