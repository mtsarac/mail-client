using MailClient.Application.Mail;

namespace MailClient.Tests;

public sealed class MailSecurityParserTests
{
    [Theory]
    [InlineData("multipart/signed; protocol=\"application/pkcs7-signature\"; boundary=x", MailCryptoStandard.SMime, false)]
    [InlineData("multipart/signed; protocol=\"application/pgp-signature\"; boundary=x", MailCryptoStandard.OpenPgp, false)]
    [InlineData("application/pkcs7-mime; smime-type=signed-data", MailCryptoStandard.SMime, false)]
    [InlineData("application/pkcs7-mime; smime-type=enveloped-data", MailCryptoStandard.SMime, true)]
    [InlineData("multipart/encrypted; protocol=\"application/pgp-encrypted\"; boundary=x", MailCryptoStandard.OpenPgp, true)]
    public void Parse_RecognizesStandardAndPurpose(string contentType, MailCryptoStandard standard, bool encrypted)
    {
        var security = MailSecurityParser.Parse([new MailHeaderResponse("Content-Type", contentType)], null);

        Assert.NotNull(security);
        Assert.Equal(encrypted ? null : standard, security.Signed);
        Assert.Equal(encrypted ? standard : null, security.Encrypted);
    }

    [Fact]
    public void Parse_DoesNotMistakeRegularMultipartOrOpaqueAttachmentForSignature()
    {
        Assert.Null(MailSecurityParser.Parse([new MailHeaderResponse("Content-Type", "multipart/mixed; boundary=x")], null));
        Assert.Null(MailSecurityParser.Parse([new MailHeaderResponse("Content-Type", "application/octet-stream; name=signature.p7s")], null));
    }

    [Fact]
    public void Parse_DetectsInlinePgpArmor()
    {
        Assert.Equal(MailCryptoStandard.OpenPgp, MailSecurityParser.Parse([], "-----BEGIN PGP SIGNED MESSAGE-----")?.Signed);
        Assert.Equal(MailCryptoStandard.OpenPgp, MailSecurityParser.Parse([], "-----BEGIN PGP MESSAGE-----")?.Encrypted);
    }
}
