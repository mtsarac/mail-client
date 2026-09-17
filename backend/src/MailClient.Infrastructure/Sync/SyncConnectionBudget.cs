using System.Collections.Concurrent;

namespace MailClient.Infrastructure.Sync;

public interface ISyncConnectionBudget
{
    Task<IDisposable> AcquireAsync(string host, int limit, CancellationToken cancellationToken);
}

public sealed class SyncConnectionBudget : ISyncConnectionBudget
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string host, int limit, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(host, _ => new SemaphoreSlim(Math.Max(1, limit), Math.Max(1, limit)));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
