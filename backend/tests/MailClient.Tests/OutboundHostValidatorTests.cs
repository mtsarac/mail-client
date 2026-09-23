using System.Net;
using MailClient.Infrastructure.Network;

namespace MailClient.Tests;

public sealed class OutboundHostValidatorTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("fc00::1")]
    [InlineData("::")]
    public async Task UnsafeHosts_AreRejected(string host)
    {
        var validator = new OutboundHostValidator(new FakeDns(IPAddress.Parse(host == "localhost" ? "127.0.0.1" : host)));
        Assert.False((await validator.ValidateAsync(host, CancellationToken.None)).Allowed);
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    public async Task PublicHosts_AreAllowed(string address)
    {
        var validator = new OutboundHostValidator(new FakeDns(IPAddress.Parse(address)));
        Assert.True((await validator.ValidateAsync("mail.example.com", CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task MixedDnsAnswer_WithAnyPrivateAddress_IsRejected()
    {
        var validator = new OutboundHostValidator(new FakeDns(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5")));
        Assert.False((await validator.ValidateAsync("mail.example.com", CancellationToken.None)).Allowed);
    }

    private sealed class FakeDns(params IPAddress[] addresses) : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(addresses);
    }
}
