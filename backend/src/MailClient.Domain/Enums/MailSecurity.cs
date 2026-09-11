// IMAP/SMTP transport security modes (None is dev/test only).
namespace MailClient.Domain.Enums;

public enum MailSecurity
{
    None,
    SslOnConnect,
    StartTls
}
