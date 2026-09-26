using MimeKit;

namespace MailClient.Application.Mail;

public enum MailCryptoStandard { SMime, OpenPgp }

public enum MailSignatureStatus { Valid, Untrusted, Invalid, Unverifiable }

public sealed record MailSecurityResponse(MailCryptoStandard? Signed, MailCryptoStandard? Encrypted);

public sealed record MailSignatureSigner(string? Name, string? Email, DateTime? SignedAt, DateTime? CertificateExpiresAt);

public sealed record MailSignatureResponse(MailCryptoStandard Standard, MailSignatureStatus Status, IReadOnlyList<MailSignatureSigner> Signers);

public sealed record MailSourceHeadersResponse(IReadOnlyList<MailHeaderResponse> Headers);

public static class MailSecurityParser
{
    public static MailSecurityResponse? Parse(IEnumerable<MailHeaderResponse> headers, string? bodyText)
    {
        MailCryptoStandard? signed = null;
        MailCryptoStandard? encrypted = null;
        var value = headers.FirstOrDefault(header =>
            string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(value) && ContentType.TryParse(value, out var contentType))
        {
            var protocol = contentType.Parameters["protocol"]?.Trim().ToLowerInvariant();
            if (contentType.IsMimeType("multipart", "signed"))
                signed = protocol switch
                {
                    "application/pkcs7-signature" or "application/x-pkcs7-signature" => MailCryptoStandard.SMime,
                    "application/pgp-signature" => MailCryptoStandard.OpenPgp,
                    _ => null
                };
            else if (contentType.IsMimeType("multipart", "encrypted") && protocol == "application/pgp-encrypted")
                encrypted = MailCryptoStandard.OpenPgp;
            else if (contentType.IsMimeType("application", "pkcs7-mime") || contentType.IsMimeType("application", "x-pkcs7-mime"))
            {
                if (string.Equals(contentType.Parameters["smime-type"], "signed-data", StringComparison.OrdinalIgnoreCase))
                    signed = MailCryptoStandard.SMime;
                else
                    encrypted = MailCryptoStandard.SMime;
            }
        }

        if (signed is null && encrypted is null && bodyText is not null)
        {
            if (bodyText.Contains("-----BEGIN PGP SIGNED MESSAGE-----", StringComparison.Ordinal))
                signed = MailCryptoStandard.OpenPgp;
            else if (bodyText.Contains("-----BEGIN PGP MESSAGE-----", StringComparison.Ordinal))
                encrypted = MailCryptoStandard.OpenPgp;
        }

        return signed is null && encrypted is null ? null : new MailSecurityResponse(signed, encrypted);
    }
}
