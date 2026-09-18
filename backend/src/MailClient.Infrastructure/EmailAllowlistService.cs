using MailClient.Application.Runtime;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Accounts;

public sealed class EmailAllowlistService(AppDbContext db, IRuntimeSettingsStore settings) : IEmailAllowlistService
{
    public async Task<bool> IsAllowedAsync(string email, CancellationToken cancellationToken)
    {
        if (!await IsEnforcedAsync(cancellationToken))
            return true;
        var normalized = Normalize(email);
        return await db.AllowlistedEmails.AsNoTracking().AnyAsync(item => item.NormalizedEmail == normalized, cancellationToken);
    }

    public async Task<bool> IsEnforcedAsync(CancellationToken cancellationToken)
    {
        // Enforcement is entirely config-driven (RuntimeSettings.Whitelist.Enabled, off by default) so it
        // can be turned on/off via the Management API in any environment, as required. Every caller that
        // applies an allowlist side effect (rejecting a signup, disabling/restoring an account, the
        // grace-period deletion sweep) goes through this one check so behavior stays consistent everywhere.
        var snapshot = await settings.GetAsync(cancellationToken);
        return snapshot.Settings.Whitelist.Enabled;
    }

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();
}
