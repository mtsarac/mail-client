using MailClient.Application.Discovery;
using MailClient.Infrastructure.Network;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace MailClient.Infrastructure.Mail;

public interface IMailConnectionValidator
{
    Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken);
    Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken);
}

public sealed class MailKitConnectionValidator(OutboundHostValidator hosts) : IMailConnectionValidator, IMailServerCandidateValidator
{
    public async Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => await ValidateCandidateAsync(candidate, cancellationToken);

    public async Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken)
    {
        if (!Allowed(candidate.Imap.Port, candidate.Imap.Security, true) || !Allowed(candidate.Smtp.Port, candidate.Smtp.Security, false)) return false;
        return (await hosts.ValidateAsync(candidate.Imap.Host, cancellationToken)).Allowed && (await hosts.ValidateAsync(candidate.Smtp.Host, cancellationToken)).Allowed;
    }

    public async Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken)
    {
        if (!await ValidateCandidateAsync(candidate, cancellationToken)) throw new InvalidOperationException("mail_server_unsafe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(candidate.Imap.Host, candidate.Imap.Port, Map(candidate.Imap.Security), timeout.Token);
            await imap.AuthenticateAsync(username, password, timeout.Token);
            await imap.DisconnectAsync(true, timeout.Token);
        }
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(candidate.Smtp.Host, candidate.Smtp.Port, Map(candidate.Smtp.Security), timeout.Token);
        await smtp.AuthenticateAsync(username, password, timeout.Token);
        await smtp.DisconnectAsync(true, timeout.Token);
    }

    private static bool Allowed(int port, Domain.Enums.MailSecurity security, bool imap) => security switch
    {
        Domain.Enums.MailSecurity.SslOnConnect => port == (imap ? 993 : 465),
        Domain.Enums.MailSecurity.StartTls => port == (imap ? 143 : 587),
        _ => false
    };
    private static SecureSocketOptions Map(Domain.Enums.MailSecurity security) => security == Domain.Enums.MailSecurity.SslOnConnect ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
}
