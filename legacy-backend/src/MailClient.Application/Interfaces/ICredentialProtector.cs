// Mailbox-password encryption seam (protect/unprotect) used by account services.
namespace MailClient.Application.Interfaces;

public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
