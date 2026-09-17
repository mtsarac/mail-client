using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MailClient.Infrastructure.Sync;

public enum SyncLockPurpose
{
    AccountSync,
    OAuthRefresh
}

public interface ISyncLock : IAsyncDisposable
{
    Guid AccountId { get; }
    SyncLockPurpose Purpose { get; }
}

public interface ISyncLockProvider
{
    Task<ISyncLock?> TryAcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken);
}

/// <summary>
/// PostgreSQL session-level advisory locks. Released automatically if the
/// connection dies. Each lock uses its own dedicated connection; the lock is
/// held for the lifetime of the returned handle.
/// </summary>
public sealed class PostgresSyncLockProvider(string connectionString, ILogger<PostgresSyncLockProvider> logger) : ISyncLockProvider
{
    public async Task<ISyncLock?> TryAcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken)
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
                return null;
            }

            return new PostgresSyncLock(connection, accountId, purpose, logger);
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sync lock acquisition failed for account {AccountId} purpose {Purpose}.", accountId, purpose);
            await connection.DisposeAsync();
            return null;
        }
    }

    internal static (int Key1, int Key2) LockKey(Guid accountId, SyncLockPurpose purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"mailclient:sync:{purpose}:{accountId:N}"));
        return (BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4));
    }

    private sealed class PostgresSyncLock(NpgsqlConnection connection, Guid accountId, SyncLockPurpose purpose, ILogger logger) : ISyncLock
    {
        public Guid AccountId => accountId;
        public SyncLockPurpose Purpose => purpose;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = connection.CreateCommand();
                    var (key1, key2) = LockKey(accountId, purpose);
                    command.CommandText = "SELECT pg_advisory_unlock(@key1, @key2)";
                    command.Parameters.AddWithValue("key1", key1);
                    command.Parameters.AddWithValue("key2", key2);
                    await command.ExecuteScalarAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Sync lock release failed for account {AccountId} purpose {Purpose}.", accountId, purpose);
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
public sealed class InMemorySyncLockProvider : ISyncLockProvider
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, SyncLockPurpose), SemaphoreSlim> Locks = new();

    public Task<ISyncLock?> TryAcquireAsync(Guid accountId, SyncLockPurpose purpose, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd((accountId, purpose), static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0, cancellationToken))
            return Task.FromResult<ISyncLock?>(null);
        return Task.FromResult<ISyncLock?>(new Handle(gate, accountId, purpose));
    }

    private sealed class Handle(SemaphoreSlim gate, Guid accountId, SyncLockPurpose purpose) : ISyncLock
    {
        public Guid AccountId => accountId;
        public SyncLockPurpose Purpose => purpose;
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
