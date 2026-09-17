using System.Diagnostics;
using MailClient.Application.Observability;
using MailClient.Application.Runtime;
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

public sealed class MailServerDiscoveryService(
    IEnumerable<IMailDiscoveryStrategy> strategies,
    IMailServerCandidateValidator validator,
    MailDiscoveryOptions options,
    IRuntimePolicyProvider? runtimePolicy = null,
    MailClientMetrics? metrics = null)
{
    public async Task<MailServerCandidate?> DiscoverAsync(string email, CancellationToken cancellationToken)
    {
        using var activity = MailClientTelemetry.StartActivity("mailclient.discovery");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var candidate = await DiscoverCoreAsync(email, cancellationToken);
            var result = candidate is null ? "not_found" : "found";
            activity?.SetTag("discovery.result", result);
            if (candidate is not null)
            {
                activity?.SetTag("discovery.source", MailClientTelemetry.DiscoverySourceName(candidate.Source));
                activity?.SetTag("mail.provider", MailClientTelemetry.Provider(candidate.Provider));
            }

            metrics?.RecordDiscovery(result, candidate?.Source, candidate?.Provider, Stopwatch.GetElapsedTime(started));
            return candidate;
        }
        catch (OperationCanceledException)
        {
            metrics?.RecordDiscovery("cancelled", null, null, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch (Exception ex)
        {
            MailClientTelemetry.MarkFailed(activity, ex);
            metrics?.RecordDiscovery("failure", null, null, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    private async Task<MailServerCandidate?> DiscoverCoreAsync(string email, CancellationToken cancellationToken)
    {
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(TimeSpan.FromSeconds(options.OverallTimeoutSeconds));
        var policy = runtimePolicy is null ? null : await runtimePolicy.GetAsync(cancellationToken);
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
                var effectiveCandidate = policy is null
                    ? candidate
                    : candidate with { AuthenticationMethods = policy.GetAuthenticationMethods(candidate.Provider) };
                if (effectiveCandidate.AuthenticationMethods.Count == 0)
                {
                    continue;
                }

                bool valid;
                try
                {
                    using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                    validationCts.CancelAfter(TimeSpan.FromSeconds(options.StrategyTimeoutSeconds));
                    valid = await validator.ValidateAsync(effectiveCandidate, validationCts.Token);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested is false && overallCts.Token.IsCancellationRequested is false)
                {
                    continue;
                }

                if (valid)
                    return effectiveCandidate;
            }
        }

        return null;
    }
}
