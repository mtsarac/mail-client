using System.IdentityModel.Tokens.Jwt;
using System.Net;
using MailClient.Application.Discovery;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Discovery;
using MailClient.Infrastructure.Network;

namespace MailClient.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task ResolveAllowedAsync_ReturnsValidatedAddress()
    {
        var validator = new OutboundHostValidator(new FakeDns(IPAddress.Parse("93.184.216.34")));

        var validated = await validator.ResolveAllowedAsync("example.com", CancellationToken.None);

        Assert.Equal("example.com", validated.Host);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), validated.Address);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    public async Task ResolveAllowedAsync_UnsafeDestination_Throws(string host)
    {
        var address = host == "localhost" ? IPAddress.Parse("127.0.0.1") : IPAddress.Parse(host);
        var validator = new OutboundHostValidator(new FakeDns(address));

        await Assert.ThrowsAsync<InvalidOperationException>(() => validator.ResolveAllowedAsync(host, CancellationToken.None));
    }

    [Fact]
    public async Task DiscoverAsync_AllStrategiesFail_ReturnsNull()
    {
        var service = new MailServerDiscoveryService(
            [new FailingStrategy(1), new FailingStrategy(2)],
            new AcceptingValidator(), new MailDiscoveryOptions());

        Assert.Null(await service.DiscoverAsync("person@example.test", CancellationToken.None));
    }

    [Fact]
    public void Jwt_Subject_IsMailAccountId_WithoutRoles()
    {
        var accountId = Guid.NewGuid();
        var issuer = new Api.Auth.JwtTokenIssuer(new Api.Auth.JwtOptions("MailClient", "MailClient", new string('k', 40), 15));

        var (token, _) = issuer.Issue(accountId);
        var subject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;

        Assert.Equal(accountId.ToString(), subject);
        Assert.DoesNotContain(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, c => c.Type == "role");
    }

    private sealed class FailingStrategy(int order) : IMailDiscoveryStrategy
    {
        public int Order => order;
        public async IAsyncEnumerable<MailServerCandidate> DiscoverAsync(string email, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class AcceptingValidator : IMailServerCandidateValidator
    {
        public Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
