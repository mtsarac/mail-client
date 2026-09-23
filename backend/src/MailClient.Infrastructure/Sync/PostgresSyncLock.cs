using System.Buffers.Binary;
using System.Security.Cryptography;
using MailClient.Application.Observability;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MailClient.Infrastructure.Sync;

public enum SyncLockPurpose
{
    AccountSync,
    OAuthRefresh
}

public enum SyncLockStatus
{
    Acquired,
    Contended,
    InfrastructureFailure
}

public interface ISyncLock : IAsyncDisposable
{
}

/// <summary>
/// Outcome of a lock attempt. Contention is normal; infrastructure failure means lock ownership
/// could not be determined and callers must not proceed as if they held the lock.
/// </summary>
public sealed class SyncLockAcquisition : IAsyncDisposable
{
    private readonly ISyncLock? _handle;

    private SyncLockAcquisition(SyncLockStatus status, ISyncLock? handle)
    {
        Status = status;
        _handle = handle;
    }

    public static SyncLockAcquisition Contended { get; } = new(SyncLockStatus.Contended, null);
    public static SyncLockAcquisition InfrastructureFailure { get; } = new(SyncLockStatus.InfrastructureFailure, null);
    public static SyncLockAcquisition Acquired(ISyncLock handle) => new(SyncLockStatus.Acquired, handle);

    public SyncLockStatus Status { get; }
    public bool IsAcquired => Status == SyncLockStatus.Acquired;

    public ValueTask DisposeAsync() => _handle?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public interface ISyncLockProvider
{
    Task<SyncLockAcquisition> TryAcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken);
}

internal static class SyncLockMetrics
{
    public static SyncLockAcquisition Record(MailClientMetrics? metrics, SyncLockPurpose purpose, SyncLockAcquisition acquisition)
    {
        metrics?.RecordLockAcquisition(
            purpose == SyncLockPurpose.OAuthRefresh ? "oauth_refresh" : "account_sync",
            acquisition.Status switch
            {
                SyncLockStatus.Acquired => "acquired",
                SyncLockStatus.Contended => "contended",
                _ => "failed"
            });
        return acquisition;
    }
}

/// <summary>
/// PostgreSQL session-level advisory locks. Released automatically if the
/// connection dies. Each lock uses its own dedicated connection; the lock is
/// held for the lifetime of the returned handle.
/// </summary>
public sealed class PostgresSyncLockProvider(
    string connectionString,
    ILogger<PostgresSyncLockProvider> logger,
    MailClientMetrics? metrics = null) : ISyncLockProvider
{
    public async Task<SyncLockAcquisition> TryAcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken) =>
        SyncLockMetrics.Record(metrics, purpose, await AcquireAsync(accountId, purpose, cancellationToken));

    private async Task<SyncLockAcquisition> AcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key1, @key2)";
            var (key1, key2) = LockKey(accountId, purpose);
            command.Parameters.AddWithValue("key1", key1);
            command.Parameters.AddWithValue("key2", key2);
            var acquired = (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
            if (!acquired)
            {
                await connection.DisposeAsync();
                return SyncLockAcquisition.Contended;
            }

            return SyncLockAcquisition.Acquired(new PostgresSyncLock(connection, key1, key2, purpose, logger));
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Distributed lock infrastructure failure for purpose {Purpose}.", purpose);
            await connection.DisposeAsync();
            return SyncLockAcquisition.InfrastructureFailure;
        }
    }

    internal static (int Key1, int Key2) LockKey(Guid accountId, SyncLockPurpose purpose)
    {
        Span<byte> source = stackalloc byte[17];
        accountId.TryWriteBytes(source);
        source[^1] = (byte)purpose;
        var hash = SHA256.HashData(source);
        return (
            BinaryPrimitives.ReadInt32LittleEndian(hash),
            BinaryPrimitives.ReadInt32LittleEndian(hash.AsSpan(sizeof(int))));
    }

    private sealed class PostgresSyncLock(
        NpgsqlConnection connection,
        int key1,
        int key2,
        SyncLockPurpose purpose,
        ILogger logger) : ISyncLock
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT pg_advisory_unlock(@key1, @key2)";
                    command.Parameters.AddWithValue("key1", key1);
                    command.Parameters.AddWithValue("key2", key2);
                    await command.ExecuteScalarAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Sync lock release failed for purpose {Purpose}.", purpose);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// Test/fallback lock provider using only in-process exclusion. Cross-instance
/// safety requires <see cref="PostgresSyncLockProvider"/> with PostgreSQL.
/// </summary>
public sealed class InMemorySyncLockProvider(MailClientMetrics? metrics = null) : ISyncLockProvider
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, SyncLockPurpose), SemaphoreSlim> Locks = new();

    public Task<SyncLockAcquisition> TryAcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd((accountId, purpose), static _ => new SemaphoreSlim(1, 1));
        var acquisition = gate.Wait(0, cancellationToken)
            ? SyncLockAcquisition.Acquired(new Handle(gate))
            : SyncLockAcquisition.Contended;
        return Task.FromResult(SyncLockMetrics.Record(metrics, purpose, acquisition));
    }

    private sealed class Handle(SemaphoreSlim gate) : ISyncLock
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
