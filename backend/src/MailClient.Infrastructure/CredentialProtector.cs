using MailClient.Application.Accounts;
using Microsoft.AspNetCore.DataProtection;

namespace MailClient.Infrastructure.Security;

public sealed class DataProtectionCredentialProtector(IDataProtectionProvider provider) : ICredentialProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("MailClient.MailCredential.v2");
    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
