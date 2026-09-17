using MailClient.Domain.Enums;

namespace MailClient.Application.Sync;

public interface ISyncClock
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemSyncClock : ISyncClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class SyncRetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay, ISyncClock clock, Func<double> jitterSource)
{
    public int MaxAttempts => maxAttempts;

    public static SyncRetryPolicy FromSettings(int maxAttempts, int baseDelaySeconds, int maxDelaySeconds, ISyncClock clock, Func<double>? jitterSource = null)
    {
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (baseDelaySeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(baseDelaySeconds));
        if (maxDelaySeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDelaySeconds));
        if (baseDelaySeconds > maxDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(baseDelaySeconds));

        return new SyncRetryPolicy(
            maxAttempts,
            TimeSpan.FromSeconds(baseDelaySeconds),
            TimeSpan.FromSeconds(maxDelaySeconds),
            clock,
            jitterSource ?? Random.Shared.NextDouble);
    }

    public bool ShouldRetry(int failedAttempts, SyncFailureCategory category, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failedAttempts);
        return !cancellationToken.IsCancellationRequested
            && category == SyncFailureCategory.Transient
            && failedAttempts < maxAttempts;
    }

    public TimeSpan DelayForAttempt(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failedAttempts);
        var exponentialSeconds = baseDelay.TotalSeconds * Math.Pow(2, failedAttempts - 1);
        var cappedSeconds = Math.Min(exponentialSeconds, maxDelay.TotalSeconds);
        var jitter = jitterSource() * cappedSeconds * 0.25;
        return TimeSpan.FromSeconds(cappedSeconds + jitter);
    }

    public DateTime NextRetryAt(int failedAttempts) =>
        clock.UtcNow.Add(DelayForAttempt(failedAttempts));

    public Task WaitForRetryAsync(int failedAttempts, CancellationToken cancellationToken) =>
        clock.DelayAsync(DelayForAttempt(failedAttempts), cancellationToken);
}
