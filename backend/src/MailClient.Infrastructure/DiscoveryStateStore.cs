using System.Collections.Concurrent;
using System.Security.Cryptography;
using MailClient.Application.Discovery;

namespace MailClient.Infrastructure.Discovery;

public sealed record DiscoveryState(string Email, MailServerCandidate Candidate, DateTime ExpiresAt);

public sealed class DiscoveryStateStore
{
    private readonly ConcurrentDictionary<string, DiscoveryState> _states = new();

    public string Store(string email, MailServerCandidate candidate, TimeSpan lifetime)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        foreach (var expired in _states.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            _states.TryRemove(expired, out _);
        _states[id] = new(email, candidate, now.Add(lifetime));
        return id;
    }

    public DiscoveryState? Get(string id) =>
        _states.TryGetValue(id, out var state) && state.ExpiresAt > DateTime.UtcNow ? state : null;

    public void Consume(string id) => _states.TryRemove(id, out _);
}
