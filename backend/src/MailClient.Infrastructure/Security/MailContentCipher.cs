using Microsoft.AspNetCore.DataProtection;

namespace MailClient.Infrastructure.Security;

/// <summary>
/// Encrypts stored mail content (currently <c>Mail.BodyHtml</c>) with ASP.NET Data Protection so a database
/// dump alone does not expose message bodies. Writers call <see cref="Protect"/> and readers <see cref="Unprotect"/>
/// (via the null-tolerant <see cref="MailContentCipherExtensions"/> when encryption is disabled). Values written by this class carry a version prefix; anything
/// without it is treated as legacy plaintext and returned unchanged, so existing rows stay readable until
/// <see cref="MailContentEncryptionBackfill"/> rewrites them.
/// </summary>
/// <remarks>
/// <c>Subject</c> and <c>BodyText</c> are intentionally not encrypted here: the PostgreSQL full-text search
/// vector and list previews are computed by the database from those columns. Protect them with volume or
/// database-level encryption at rest.
/// </remarks>
public sealed class MailContentCipher(IDataProtectionProvider provider)
{
    public const string Prefix = "enc:v1:";
    private readonly IDataProtector _protector = provider.CreateProtector("MailClient.MailContent.v1");

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string value) =>
        value.Length == 0 || IsProtected(value) ? value : Prefix + _protector.Protect(value);

    /// <summary>Returns plaintext. Unreadable ciphertext (for example after key loss) yields an empty string.</summary>
    public string Unprotect(string value)
    {
        if (!IsProtected(value))
            return value;
        try
        {
            return _protector.Unprotect(value[Prefix.Length..]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "";
        }
    }
}

public static class MailContentCipherExtensions
{
    /// <summary>Passes the value through unchanged when encryption is disabled (<paramref name="cipher"/> is null).</summary>
    public static string ProtectContent(this MailContentCipher? cipher, string value) => cipher?.Protect(value) ?? value;

    /// <summary>Returns plaintext even when encryption has since been disabled, so stored ciphertext stays readable.</summary>
    public static string UnprotectContent(this MailContentCipher? cipher, string value) =>
        cipher is not null ? cipher.Unprotect(value) : MailContentCipher.IsProtected(value) ? "" : value;
}
