using MailClient.Infrastructure.Authentication;

namespace MailClient.Tests;

public sealed class SessionSecurityTests
{
    [Fact]
    public void RefreshToken_HashDoesNotContainRawToken()
    {
        var token = RefreshTokenService.GenerateToken();
        var hash = RefreshTokenService.HashToken(token);

        Assert.NotEqual(token, hash);
        Assert.Equal(64, hash.Length);
        Assert.True(RefreshTokenService.FixedTimeEquals(hash, token));
    }

    [Fact]
    public void RefreshToken_GeneratorProducesIndependentTokens()
    {
        Assert.NotEqual(RefreshTokenService.GenerateToken(), RefreshTokenService.GenerateToken());
    }
}
