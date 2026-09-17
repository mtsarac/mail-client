using MailClient.Application.Discovery;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.OAuth;

namespace MailClient.Infrastructure.Discovery;

public sealed class KnownProviderStrategy(IEnumerable<IOAuthProvider> oauthProviders) : IMailDiscoveryStrategy
{
    public KnownProviderStrategy() : this(Array.Empty<IOAuthProvider>())
    {
    }

    public int Order => 1;
    public async IAsyncEnumerable<MailServerCandidate> DiscoverAsync(string email, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var domain = email.Split('@').LastOrDefault()?.ToLowerInvariant();
        MailServerCandidate? candidate = domain switch
        {
            "gmail.com" or "googlemail.com" => Candidate(MailProvider.Google, "imap.gmail.com", "smtp.gmail.com"),
            "outlook.com" or "hotmail.com" or "live.com" => Candidate(MailProvider.Microsoft, "outlook.office365.com", "smtp.office365.com", 587, MailSecurity.StartTls),
            "icloud.com" or "me.com" or "mac.com" => Candidate(MailProvider.ICloud, "imap.mail.me.com", "smtp.mail.me.com"),
            "yahoo.com" => Candidate(MailProvider.Yahoo, "imap.mail.yahoo.com", "smtp.mail.yahoo.com"),
            _ => null
        };
        if (candidate is not null) yield return candidate;
        await Task.CompletedTask;
    }
    private MailServerCandidate Candidate(MailProvider provider, string imap, string smtp, int smtpPort = 465, MailSecurity smtpSecurity = MailSecurity.SslOnConnect)
    {
        var methods = new List<AuthenticationMethod> { AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword };
        if (oauthProviders.Any(p => p.Provider == provider && p.IsConfigured))
            methods.Add(AuthenticationMethod.OAuth2);
        return new(provider, new(imap, 993, MailSecurity.SslOnConnect), new(smtp, smtpPort, smtpSecurity), methods, DiscoverySource.KnownProvider);
    }
}

public sealed class HeuristicDiscoveryStrategy : IMailDiscoveryStrategy
{
    public int Order => 5;
    public async IAsyncEnumerable<MailServerCandidate> DiscoverAsync(string email, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var domain = email.Split('@').LastOrDefault();
        if (!string.IsNullOrWhiteSpace(domain))
        {
            yield return new(MailProvider.Custom, new($"imap.{domain}", 993, MailSecurity.SslOnConnect), new($"smtp.{domain}", 465, MailSecurity.SslOnConnect), [AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword], DiscoverySource.Heuristic);
            yield return new(MailProvider.Custom, new($"mail.{domain}", 993, MailSecurity.SslOnConnect), new($"mail.{domain}", 465, MailSecurity.SslOnConnect), [AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword], DiscoverySource.Heuristic);
        }
        await Task.CompletedTask;
    }
}
