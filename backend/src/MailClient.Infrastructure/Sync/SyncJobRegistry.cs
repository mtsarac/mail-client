using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Sync;

/// <summary>Process-local statuses. Unknown IDs after a restart or expiry are never reported as successes.</summary>
internal sealed class SyncJobRegistry(ISyncClock clock)
{
    private const int MaxJobs = 4096;
    private static readonly TimeSpan ActiveLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromHours(1);
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly Dictionary<SyncRequest, List<Guid>> _queued = [];

    public void EnsureCapacity()
    {
        Prune();
        if (_jobs.Count < MaxJobs)
            return;
        var oldestTerminal = _jobs
            .Where(entry => entry.Value.Status is "failed" or "succeeded")
            .MinBy(entry => entry.Value.UpdatedAt);
        if (oldestTerminal.Value is null)
            throw new SyncQueueFullException();
        _jobs.Remove(oldestTerminal.Key);
    }

    public Guid Create(SyncRequest request)
    {
        var id = Guid.NewGuid();
        _jobs.Add(id, new Job(request.AccountId, "queued", null, clock.UtcNow));
        if (!_queued.TryGetValue(request, out var ids))
        {
            ids = [];
            _queued.Add(request, ids);
        }
        ids.Add(id);
        return id;
    }

    public SyncJobStatus? Get(Guid accountId, Guid jobId)
    {
        Prune();
        return _jobs.TryGetValue(jobId, out var job) && job.AccountId == accountId
            ? new SyncJobStatus(jobId, job.Status, job.ErrorCode)
            : null;
    }

    public IReadOnlyList<Guid> Claim(SyncRequest request)
    {
        if (!_queued.Remove(request, out var ids))
            return [];
        foreach (var id in ids)
            SetStatus(id, "running", null);
        return ids;
    }

    public void Defer(SyncRequest request, IReadOnlyList<Guid> ids)
    {
        if (!_queued.TryGetValue(request, out var queued))
        {
            queued = [];
            _queued.Add(request, queued);
        }
        foreach (var id in ids)
        {
            SetStatus(id, "queued", null);
            queued.Add(id);
        }
    }


    public void Complete(IReadOnlyList<Guid> ids, string? errorCode)
    {
        foreach (var id in ids)
            SetStatus(id, errorCode is null ? "succeeded" : "failed", errorCode);
        Prune();
    }

    public void FailQueued(SyncRequest request, string errorCode)
    {
        if (_queued.Remove(request, out var ids))
            Complete(ids, errorCode);
    }

    public void FailAll(string errorCode)
    {
        foreach (var job in _jobs.Values)
        {
            if (job.Status is "queued" or "running")
            {
                job.Status = "failed";
                job.ErrorCode = errorCode;
                job.UpdatedAt = clock.UtcNow;
            }
        }
        _queued.Clear();
    }

    private void SetStatus(Guid id, string status, string? errorCode)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.Status is "failed" or "succeeded")
            return;
        job.Status = status;
        job.ErrorCode = errorCode;
        job.UpdatedAt = clock.UtcNow;
    }

    private void Prune()
    {
        var now = clock.UtcNow;
        foreach (var job in _jobs.Values)
        {
            if ((job.Status is "queued" or "running") && now - job.CreatedAt >= ActiveLifetime)
            {
                job.Status = "failed";
                job.ErrorCode = "sync_interrupted";
                job.UpdatedAt = now;
            }
        }
        foreach (var (id, job) in _jobs.ToArray())
        {
            if ((job.Status is "failed" or "succeeded") && now - job.UpdatedAt >= TerminalRetention)
                _jobs.Remove(id);
        }
        foreach (var request in _queued.Keys.ToArray())
        {
            _queued[request].RemoveAll(id => !_jobs.ContainsKey(id) || _jobs[id].Status != "queued");
            if (_queued[request].Count == 0)
                _queued.Remove(request);
        }
    }

    private sealed class Job(Guid accountId, string status, string? errorCode, DateTime createdAt)
    {
        public Guid AccountId { get; } = accountId;
        public string Status { get; set; } = status;
        public string? ErrorCode { get; set; } = errorCode;
        public DateTime CreatedAt { get; } = createdAt;
        public DateTime UpdatedAt { get; set; } = createdAt;
    }
}
