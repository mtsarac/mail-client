using MailClient.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Persistence;

public sealed class DatabaseHealthProbe(AppDbContext db, ILogger<DatabaseHealthProbe> logger) : IHealthProbe
{
    public async Task<bool> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.CloseConnectionAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database health check failed.");
            return false;
        }
    }
}
