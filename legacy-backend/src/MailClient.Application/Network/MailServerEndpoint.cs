using MailClient.Domain.Enums;

// Value object describing an IMAP/SMTP host, port, and security mode.
namespace MailClient.Application.Network;

public sealed record MailServerEndpoint(string Host, int Port, MailSecurity Security);
