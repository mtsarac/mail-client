using MailClient.Application.Runtime;
using MailClient.Domain;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

/// <summary>
/// Keeps mail-account access in sync with the production email allowlist and, after the configured
/// grace period, permanently deletes accounts that stayed off the list. Management endpoints already
/// apply the disable/restore side effects synchronously for the email they touch; this pass is the
/// safety net that catches every other case (the feature being toggled off, an account that was
/// already disabled before the list existed, or the grace-period deletion itself).
/// </summary>
public sealed class AllowlistReconciliationService(
    IServiceScopeFactory scopes,
    ILogger<AllowlistReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = 15;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var provider = scope.ServiceProvider;
                var settings = provider.GetRequiredService<IRuntimeSettingsStore>();
                var snapshot = await settings.GetAsync(stoppingToken);
                intervalMinutes = snapshot.Settings.Whitelist.ReconciliationIntervalMinutes;
                var enforced = await provider.GetRequiredService<IEmailAllowlistService>().IsEnforcedAsync(stoppingToken);
                await ReconcileAsync(provider, snapshot.Settings.Whitelist, enforced, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Allowlist reconciliation failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private static async Task ReconcileAsync(IServiceProvider provider, RuntimeWhitelistSettings settings, bool enforced, CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<AppDbContext>();
        var audit = provider.GetRequiredService<AuditLogger>();
        var now = DateTime.UtcNow;

        if (!enforced)
        {
            // Feature is off: restore everyone who was disabled specifically for falling off the allowlist.
            var revoked = await db.MailAccounts
                .Where(account => account.Status == MailAccountStatus.Disabled && account.AccessRevokedAt != null)
                .ToListAsync(cancellationToken);
            foreach (var account in revoked)
                Restore(account, now);
            if (revoked.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                foreach (var account in revoked)
                    await audit.WriteAsync(account.Id, AuditActions.MailAccountAccessRestored, "MailAccount", account.Id.ToString(), new { reason = "allowlist_disabled" }, null, cancellationToken);
            }

            return;
        }

        var allowlist = await db.AllowlistedEmails.Select(item => item.NormalizedEmail).ToListAsync(cancellationToken);

        var toDisable = await db.MailAccounts
            .Where(account => account.Status != MailAccountStatus.Disabled && !allowlist.Contains(account.NormalizedEmailAddress))
            .ToListAsync(cancellationToken);
        foreach (var account in toDisable)
        {
            account.Status = MailAccountStatus.Disabled;
            account.AccessRevokedAt = now;
            account.UpdatedAt = now;
        }

        var toRestore = await db.MailAccounts
            .Where(account => account.Status == MailAccountStatus.Disabled && account.AccessRevokedAt != null && allowlist.Contains(account.NormalizedEmailAddress))
            .ToListAsync(cancellationToken);
        foreach (var account in toRestore)
            Restore(account, now);

        if (toDisable.Count > 0 || toRestore.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var account in toDisable)
                await audit.WriteAsync(account.Id, AuditActions.MailAccountAccessRevoked, "MailAccount", account.Id.ToString(), null, null, cancellationToken);
            foreach (var account in toRestore)
                await audit.WriteAsync(account.Id, AuditActions.MailAccountAccessRestored, "MailAccount", account.Id.ToString(), null, null, cancellationToken);
        }

        var deadline = now.AddDays(-settings.DataRetentionGraceDays);
        var toDelete = await db.MailAccounts
            .Where(account => account.Status == MailAccountStatus.Disabled && account.AccessRevokedAt != null && account.AccessRevokedAt < deadline)
            .Select(account => account.Id)
            .ToListAsync(cancellationToken);
        if (toDelete.Count == 0)
            return;
        var deletion = provider.GetRequiredService<AccountDeletionService>();
        foreach (var accountId in toDelete)
        {
            if (await deletion.DeleteAsync(accountId, cancellationToken))
                await audit.WriteAsync(null, AuditActions.MailAccountDeletedGracePeriodExpired, "MailAccount", accountId.ToString(), null, null, cancellationToken);
        }
    }

    private static void Restore(Domain.Entities.MailAccount account, DateTime now)
    {
        account.Status = MailAccountStatus.Active;
        account.AccessRevokedAt = null;
        account.UpdatedAt = now;
    }
}
