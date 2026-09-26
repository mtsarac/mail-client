using MailClient.Application.Mail;

namespace MailClient.Tests;

public sealed class MailAuthenticationParserTests
{
    [Fact]
    public void Parse_ReturnsMethodsAndAuthservId_WithComments()
    {
        var result = MailAuthenticationParser.Parse([
            new MailHeaderResponse(
                "Authentication-Results",
                "mx.example.test; spf=softfail (sender not authorized) smtp.mailfrom=sender.test; dkim=pass header.d=sender.test; dmarc=fail (p=reject) header.from=sender.test")
        ]);

        Assert.Equal("mx.example.test", result!.AuthservId);
        Assert.Equal("softfail", result.Spf);
        Assert.Equal("pass", result.Dkim);
        Assert.Equal("fail", result.Dmarc);
    }

    [Fact]
    public void Parse_PrefersPassingSignature_WhenMessageHasMultipleDkimResults()
    {
        var result = MailAuthenticationParser.Parse([
            new MailHeaderResponse(
                "Authentication-Results",
                "mx.example.test; dkim=fail header.d=old.test; dkim=pass header.d=current.test; spf=neutral smtp.mailfrom=current.test; dmarc=policy header.from=current.test")
        ]);

        Assert.Equal("pass", result!.Dkim);
        Assert.Equal("neutral", result.Spf);
        Assert.Equal("policy", result.Dmarc);
    }

    [Fact]
    public void Parse_MissingOrMalformedHeader_ReturnsNull()
    {
        Assert.Null(MailAuthenticationParser.Parse([]));
        Assert.Null(MailAuthenticationParser.Parse([
            new MailHeaderResponse("Authentication-Results", "not valid ;;; =")
        ]));
    }

    [Fact]
    public void Parse_UsesOnlyTopmostHeader()
    {
        var result = MailAuthenticationParser.Parse([
            new MailHeaderResponse("Authentication-Results", "broken"),
            new MailHeaderResponse("Authentication-Results", "forged.example; spf=pass smtp.mailfrom=attacker.test")
        ]);

        Assert.Null(result);
    }
}
