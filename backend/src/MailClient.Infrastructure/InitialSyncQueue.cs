using System.Collections.Concurrent;
using System.Threading.Channels;
using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Mail;

public sealed class InitialSyncQueue
{
    private readonly Channel<SyncRequest> _channel = Channel.CreateUnbounded<SyncRequest>();
    private readonly ConcurrentDictionary<SyncRequest, byte> _pending = new();

    public async ValueTask EnqueueAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        if (!_pending.TryAdd(request, 0)) return;
        await _channel.Writer.WriteAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<SyncRequest> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _pending.TryRemove(request, out _);
            yield return request;
        }
    }
}
