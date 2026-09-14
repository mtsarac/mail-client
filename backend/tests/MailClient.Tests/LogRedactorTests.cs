using MailClient.Infrastructure.Observability;

namespace MailClient.Tests;

public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("appSpecificPassword")]
    [InlineData("refreshToken")]
    [InlineData("providerRefreshToken")]
    [InlineData("authorizationCode")]
    [InlineData("codeVerifier")]
    [InlineData("clientSecret")]
    [InlineData("credential")]
    public void Redact_RemovesSecret(string key)
    {
        var redacted = LogRedactor.Redact($"{{\"{key}\":\"secret-value\",\"safe\":\"visible\"}}");
        Assert.DoesNotContain("secret-value", redacted);
        Assert.Contains("visible", redacted);
    }
}
