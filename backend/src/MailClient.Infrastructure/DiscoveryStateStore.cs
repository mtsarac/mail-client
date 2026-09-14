using System.Collections.Concurrent;
using MailClient.Application.Discovery;

namespace MailClient.Infrastructure.Discovery;

public sealed record DiscoveryState(string Email, MailServerCandidate Candidate, DateTime ExpiresAt);

public sealed class DiscoveryStateStore
{
    private readonly ConcurrentDictionary<string, DiscoveryState> _states = new();
    public string Store(string email, MailServerCandidate candidate, TimeSpan lifetime)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _states[id] = new(email, candidate, DateTime.UtcNow.Add(lifetime));
        return id;
    }
    public DiscoveryState? Take(string id)
    {
        if (!_states.TryRemove(id, out var state) || state.ExpiresAt <= DateTime.UtcNow) return null;
        return state;
    }
}
