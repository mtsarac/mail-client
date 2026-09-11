using MailClient.Application.Interfaces;
using Microsoft.AspNetCore.DataProtection;

// Encrypts mailbox passwords with ASP.NET Core Data Protection.
namespace MailClient.Infrastructure.Security;

public sealed class DataProtectionCredentialProtector(IDataProtectionProvider provider) : ICredentialProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("MailClient.MailAccountCredentials.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
