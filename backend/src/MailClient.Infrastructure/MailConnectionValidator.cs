using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Infrastructure.Network;

namespace MailClient.Infrastructure.Mail;

public interface IMailConnectionValidator
{
    Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken);
    Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken);
}

public sealed class MailKitConnectionValidator(
    OutboundHostValidator hosts,
    MailConnectionHelper connections) : IMailConnectionValidator, IMailServerCandidateValidator
{
    public Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) =>
        ValidateCandidateAsync(candidate, cancellationToken);

    public async Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken)
    {
        if (!Allowed(candidate.Imap.Port, candidate.Imap.Security, true) || !Allowed(candidate.Smtp.Port, candidate.Smtp.Security, false)) return false;
        return (await hosts.ValidateAsync(candidate.Imap.Host, cancellationToken)).Allowed && (await hosts.ValidateAsync(candidate.Smtp.Host, cancellationToken)).Allowed;
    }

    public async Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken)
    {
        if (!await ValidateCandidateAsync(candidate, cancellationToken)) throw new InvalidOperationException("mail_server_unsafe");
        await connections.WithImapAsync(
            new MailServerEndpoint(candidate.Imap.Host, candidate.Imap.Port, candidate.Imap.Security),
            username, password, "ValidateImap",
            static (_, _) => Task.FromResult(true), cancellationToken);
        await connections.WithSmtpAsync(
            new MailServerEndpoint(candidate.Smtp.Host, candidate.Smtp.Port, candidate.Smtp.Security),
            username, password, "ValidateSmtp",
            static (_, _) => Task.FromResult(true), cancellationToken);
    }

    private static bool Allowed(int port, Domain.Enums.MailSecurity security, bool imap) => security switch
    {
        Domain.Enums.MailSecurity.SslOnConnect => port == (imap ? 993 : 465),
        Domain.Enums.MailSecurity.StartTls => port == (imap ? 143 : 587),
        _ => false
    };
}
