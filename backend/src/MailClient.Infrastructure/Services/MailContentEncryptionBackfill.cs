using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

/// <summary>
/// One-off startup pass that rewrites legacy plaintext <c>Mail.BodyHtml</c> rows through the content cipher.
/// Idempotent: rows already carrying the cipher prefix are skipped, and it exits when none are left.
/// </summary>
public sealed class MailContentEncryptionBackfill(
    IServiceScopeFactory scopes,
    MailContentCipher cipher,
    ILogger<MailContentEncryptionBackfill> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var total = await RunAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), cipher, stoppingToken);
            if (total > 0)
                logger.LogInformation("Encrypted stored HTML bodies of {Count} mails.", total);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mail content encryption backfill failed; it will retry on next start.");
        }
    }

    internal static async Task<int> RunAsync(AppDbContext db, MailContentCipher cipher, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var ids = await db.Database
                .SqlQuery<Guid>($"""
                    SELECT "Id" AS "Value" FROM "Mails"
                    WHERE "BodyHtml" <> '' AND "BodyHtml" NOT LIKE {MailContentCipher.Prefix + "%"}
                    ORDER BY "Id" LIMIT {BatchSize}
                    """)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
                return total;
            var mails = await db.Mails.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var mail in mails)
                mail.BodyHtml = cipher.Protect(mail.BodyHtml);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            total += mails.Count;
        }
    }
}
