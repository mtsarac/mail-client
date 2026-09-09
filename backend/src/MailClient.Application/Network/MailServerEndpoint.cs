using MailClient.Domain.Enums;

namespace MailClient.Application.Network;

public sealed record MailServerEndpoint(string Host, int Port, MailSecurity Security);
