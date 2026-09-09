using System.Net;
using MailClient.Application.Interfaces;
using MailClient.Infrastructure.Network;

namespace MailClient.Infrastructure.Tests.Network;

public class OutboundHostValidatorTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00::1234")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CheckLiteralHost_RejectsBlockedValues(string? host)
    {
        var validator = new OutboundHostValidator(new FakeDnsResolver(_ => []));

        Assert.False(validator.CheckLiteralHost(host).Allowed);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("mail.example.com")]
    public void CheckLiteralHost_AllowsPublicValues(string host)
    {
        var validator = new OutboundHostValidator(new FakeDnsResolver(_ => []));

        Assert.True(validator.CheckLiteralHost(host).Allowed);
    }

    [Fact]
    public async Task CheckAsync_RejectsHostnameResolvingToPrivateIp()
    {
        var validator = new OutboundHostValidator(new FakeDnsResolver(_ =>
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.1.2.3")]));

        var result = await validator.CheckAsync("mixed.example.com", CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task CheckAsync_AcceptsHostnameResolvingToPublicIps()
    {
        var validator = new OutboundHostValidator(new FakeDnsResolver(_ =>
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")]));

        var result = await validator.CheckAsync("public.example.com", CancellationToken.None);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task CheckAsync_RejectsWhenDnsFails()
    {
        var validator = new OutboundHostValidator(new FailingDnsResolver());

        var result = await validator.CheckAsync("missing.example.com", CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task CheckAsync_RejectsEmptyResolution()
    {
        var validator = new OutboundHostValidator(new FakeDnsResolver(_ => []));

        var result = await validator.CheckAsync("empty.example.com", CancellationToken.None);

        Assert.False(result.Allowed);
    }

    private sealed class FakeDnsResolver(Func<string, IPAddress[]> resolve) : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(resolve(host));
    }

    private sealed class FailingDnsResolver : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException());
    }
}
