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

public sealed class MailServerDiscoveryService(IEnumerable<IMailDiscoveryStrategy> strategies, IMailServerCandidateValidator validator, MailDiscoveryOptions options)
{
    public async Task<MailServerCandidate?> DiscoverAsync(string email, CancellationToken cancellationToken)
    {
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(TimeSpan.FromSeconds(options.OverallTimeoutSeconds));
        foreach (var strategy in strategies.OrderBy(item => item.Order))
        {
            List<MailServerCandidate> candidates;
            try
            {
                using var strategyCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                strategyCts.CancelAfter(TimeSpan.FromSeconds(options.StrategyTimeoutSeconds));
                candidates = await strategy.DiscoverAsync(email, strategyCts.Token).ToListAsync(strategyCts.Token);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false && overallCts.Token.IsCancellationRequested is false)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                bool valid;
                try
                {
                    using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                    validationCts.CancelAfter(TimeSpan.FromSeconds(options.StrategyTimeoutSeconds));
                    valid = await validator.ValidateAsync(candidate, validationCts.Token);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested is false && overallCts.Token.IsCancellationRequested is false)
                {
                    continue;
                }

                if (valid)
                    return candidate;
            }
        }

        return null;
    }
}
