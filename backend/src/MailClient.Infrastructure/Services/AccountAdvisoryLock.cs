using System.Security.Cryptography;
using System.Text;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

internal sealed class AccountAdvisoryLock(AppDbContext db, long key, bool ownsConnection) : IAsyncDisposable
{
    public static async Task<AccountAdvisoryLock> AcquireAsync(AppDbContext db, Guid accountId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
            return new AccountAdvisoryLock(db, 0, ownsConnection: false);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("account:" + accountId.ToString("N"))), 0);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", [key], cancellationToken);
            return new AccountAdvisoryLock(db, key, ownsConnection: true);
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
