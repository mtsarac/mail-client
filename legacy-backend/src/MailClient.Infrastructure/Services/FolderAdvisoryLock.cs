using System.Security.Cryptography;
using System.Text;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// PostgreSQL advisory lock serializing per-folder sync across instances.
namespace MailClient.Infrastructure.Services;

internal sealed class FolderAdvisoryLock(AppDbContext db, long key, bool ownsConnection) : IAsyncDisposable
{
    public static async Task<FolderAdvisoryLock> AcquireAsync(AppDbContext db, Guid folderId, CancellationToken cancellationToken)
    {
        // Non-relational providers (EF InMemory in tests) have no connection to
        // lock on; the lock is a no-op there.
        if (!db.Database.IsRelational())
            return new FolderAdvisoryLock(db, 0, ownsConnection: false);

        // Blocking pg_advisory_lock: contention only ever involves the same
        // folder across instances and each critical section is one short sync
        // pass, so blocking keeps the code simple and cannot lose wakeups.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(folderId.ToString("N"))), 0);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", [key], cancellationToken);
            return new FolderAdvisoryLock(db, key, ownsConnection: true);
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!ownsConnection)
            return;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", [key], CancellationToken.None);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
