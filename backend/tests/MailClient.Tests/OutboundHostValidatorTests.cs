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
    public async Task UnsafeHosts_AreRejected(string host)
    {
        var validator = new OutboundHostValidator(new FakeDns(IPAddress.Parse(host == "localhost" ? "127.0.0.1" : host)));
        Assert.False((await validator.ValidateAsync(host, CancellationToken.None)).Allowed);
    }

    private sealed class FakeDns(params IPAddress[] addresses) : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(addresses);
    }
}
