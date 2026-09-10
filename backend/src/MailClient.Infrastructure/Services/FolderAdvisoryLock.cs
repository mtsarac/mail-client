using System.Security.Cryptography;
using System.Text;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

internal sealed class FolderAdvisoryLock(AppDbContext db, long key) : IAsyncDisposable
{
    public static async Task<FolderAdvisoryLock> AcquireAsync(AppDbContext db, Guid folderId, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(folderId.ToString("N"))), 0);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", [key], cancellationToken);
        return new FolderAdvisoryLock(db, key);
    }

    public async ValueTask DisposeAsync()
    {
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
