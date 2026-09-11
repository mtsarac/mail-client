using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace MailClient.Api.Auth;

public static class DataProtectionSetup
{
    public static X509Certificate2? Configure(
        IDataProtectionBuilder builder,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var certificatePath = configuration["DataProtection:CertificatePath"];
        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
                throw new InvalidOperationException(
                    "Production requires DataProtection key encryption. Configure DataProtection:CertificatePath and DataProtection:CertificatePassword.");
            return null;
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath, configuration["DataProtection:CertificatePassword"]);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "DataProtection certificate could not be loaded. Check DataProtection:CertificatePath and DataProtection:CertificatePassword.", ex);
        }

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("DataProtection certificate has no private key and cannot protect the key ring.");
        builder.ProtectKeysWithCertificate(certificate);
        return certificate;
    }
}
