using MailClient.Domain.Enums;

namespace MailClient.Application.Discovery;

public sealed record MailEndpoint(string Host, int Port, MailSecurity Security);
public sealed record MailServerCandidate(MailProvider Provider, MailEndpoint Imap, MailEndpoint Smtp, IReadOnlyList<AuthenticationMethod> AuthenticationMethods, DiscoverySource Source, string? UsernameFormat = null);

public interface IMailDiscoveryStrategy
{
    int Order { get; }
    IAsyncEnumerable<MailServerCandidate> DiscoverAsync(string email, CancellationToken cancellationToken);
}

public interface IMailServerCandidateValidator
{
    Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken);
}

public sealed class MailServerDiscoveryService(IEnumerable<IMailDiscoveryStrategy> strategies, IMailServerCandidateValidator validator)
{
    public async Task<MailServerCandidate?> DiscoverAsync(string email, CancellationToken cancellationToken)
    {
        foreach (var strategy in strategies.OrderBy(item => item.Order))
            await foreach (var candidate in strategy.DiscoverAsync(email, cancellationToken).WithCancellation(cancellationToken))
                if (await validator.ValidateAsync(candidate, cancellationToken)) return candidate;
        return null;
    }
}
