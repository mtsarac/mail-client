using MailClient.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace MailClient.Tests;

public sealed class MailContentCipherTests
{
    private static MailContentCipher Cipher() => new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Protect_RoundTripsAndPrefixesCiphertext()
    {
        var cipher = Cipher();

        var stored = cipher.Protect("<p>secret</p>");

        Assert.StartsWith(MailContentCipher.Prefix, stored);
        Assert.DoesNotContain("secret", stored);
        Assert.Equal("<p>secret</p>", cipher.Unprotect(stored));
    }

    [Fact]
    public void Unprotect_ReturnsLegacyPlaintextUnchanged_AndProtectIsIdempotent()
    {
        var cipher = Cipher();

        Assert.Equal("<p>old</p>", cipher.Unprotect("<p>old</p>"));
        var once = cipher.Protect("x");
        Assert.Equal(once, cipher.Protect(once));
        Assert.Equal("", cipher.Protect(""));
    }

    [Fact]
    public void Unprotect_WithDifferentKeyOrTamperedValue_ReturnsEmpty()
    {
        var stored = Cipher().Protect("secret");

        Assert.Equal("", Cipher().Unprotect(stored));
        Assert.Equal("", Cipher().Unprotect(MailContentCipher.Prefix + "not-a-payload"));
    }

    [Fact]
    public void Extensions_PassThroughWhenDisabled_AndNeverExposeCiphertext()
    {
        MailContentCipher? disabled = null;

        Assert.Equal("<p>x</p>", disabled.ProtectContent("<p>x</p>"));
        Assert.Equal("<p>x</p>", disabled.UnprotectContent("<p>x</p>"));
        Assert.Equal("", disabled.UnprotectContent(Cipher().Protect("<p>x</p>")));
    }
}
