using MailClient.Domain.Enums;
using MailKit.Security;

namespace MailClient.Infrastructure.Services;

internal static class MailSecurityMapper
{
    internal static SecureSocketOptions ToSocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.None => SecureSocketOptions.None,
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => throw new ArgumentOutOfRangeException(nameof(security))
    };
}
