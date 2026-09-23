namespace MailClient.Application.Sync;

public sealed class SyncQueueFullException : InvalidOperationException
{
    public SyncQueueFullException()
        : base("sync_queue_full")
    {
    }
}

public enum SyncEnqueueResult
{
    Enqueued,
    Coalesced,
    Rejected,
    EnqueuedAfterShedding
}

/// <summary>
/// Bounded priority queue for sync work. User work is never silently dropped;
/// background periodic work is coalesced or shed under backpressure.
/// </summary>
public sealed class SyncScheduleQueue
{
    private readonly object _gate = new();
    private readonly PriorityQueue<ScheduledSyncRequest, SyncQueuePriority> _pending = new();
    private readonly Dictionary<SyncRequest, ScheduledSyncRequest> _byRequest = [];
    private long _sequence;
    private int _capacity;

    public SyncScheduleQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Capacity
    {
        get { lock (_gate) return _capacity; }
    }

    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }

    public void UpdateCapacity(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        lock (_gate)
        {
            _capacity = capacity;
        }
    }

    public long NextSequence()
    {
        lock (_gate)
        {
            return ++_sequence;
        }
    }

    public SyncEnqueueResult Enqueue(ScheduledSyncRequest request)
    {
        lock (_gate)
        {
            if (_byRequest.TryGetValue(request.Request, out var existing))
            {
                if (request.Priority < existing.Priority)
                {
                    _pending.Remove(existing, out _, out _);
                    _byRequest[request.Request] = request;
                    _pending.Enqueue(request, QueuePriority(request));
                }

                return SyncEnqueueResult.Coalesced;
            }

            var result = SyncEnqueueResult.Enqueued;
            if (_pending.Count >= _capacity)
            {
                if (request.Priority != SyncPriority.UserRequested)
                    return SyncEnqueueResult.Rejected;

                if (!TryShedLowestBackgroundWork())
                    throw new SyncQueueFullException();
                result = SyncEnqueueResult.EnqueuedAfterShedding;
            }

            _byRequest[request.Request] = request;
            _pending.Enqueue(request, QueuePriority(request));
            return result;
        }
    }

    public bool TryDequeue(out ScheduledSyncRequest request)
    {
        lock (_gate)
        {
            if (!_pending.TryDequeue(out var next, out _))
            {
                request = null!;
                return false;
            }

            _byRequest.Remove(next.Request);
            request = next;
            return true;
        }
    }

    public bool Contains(SyncRequest request)
    {
        lock (_gate)
        {
            return _byRequest.ContainsKey(request);
        }
    }

    public IReadOnlyList<ScheduledSyncRequest> Drain()
    {
        lock (_gate)
        {
            var drained = new List<ScheduledSyncRequest>();
            while (_pending.TryDequeue(out var next, out _))
            {
                _byRequest.Remove(next.Request);
                drained.Add(next);
            }

            return drained;
        }
    }

    /// <summary>Drops the periodic item that would run last (lowest priority, newest), so user work fits.</summary>
    private bool TryShedLowestBackgroundWork()
    {
        var victim = _pending.UnorderedItems
            .Where(item => item.Element.Priority is SyncPriority.PeriodicInbox or SyncPriority.PeriodicOtherFolder)
            .Select(item => item.Element)
            .MaxBy(QueuePriority);
        if (victim is null)
            return false;

        _pending.Remove(victim, out _, out _);
        _byRequest.Remove(victim.Request);
        return true;
    }

    private static SyncQueuePriority QueuePriority(ScheduledSyncRequest request) =>
        new((int)request.Priority, request.Sequence);

    private readonly record struct SyncQueuePriority(int Priority, long Sequence) : IComparable<SyncQueuePriority>
    {
        public int CompareTo(SyncQueuePriority other)
        {
            var priority = Priority.CompareTo(other.Priority);
            return priority != 0 ? priority : Sequence.CompareTo(other.Sequence);
        }
    }
}
