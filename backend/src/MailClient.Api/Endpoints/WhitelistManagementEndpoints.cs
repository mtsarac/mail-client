using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Runtime;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record AllowlistEmailResponse(string Email, DateTime AddedAt);
public sealed record AddAllowlistEmailsRequest(IReadOnlyList<string> Emails);
public sealed record AddAllowlistEmailsResponse(IReadOnlyList<string> Added);

public static class WhitelistManagementEndpoints
{
    public static void MapWhitelistManagementEndpoints(this WebApplication app)
    {
        var whitelist = app.MapGroup("/api/management/whitelist").AddEndpointFilter<ManagementApiKeyFilter>().WithTags("Management");

        whitelist.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var emails = await db.AllowlistedEmails.AsNoTracking()
                .OrderBy(item => item.Email)
                .Select(item => new AllowlistEmailResponse(item.Email, item.AddedAt))
                .ToListAsync(ct);
            return Results.Ok(emails);
        }).WithName("ListAllowlistEmails").WithSummary("List allowlisted emails").WithDescription("Requires the X-Management-Key header.")
            .Produces<List<AllowlistEmailResponse>>().ProblemCodes(401, "management_unauthorized");

        whitelist.MapPost("/emails", async (AddAllowlistEmailsRequest request, AppDbContext db, IEmailAllowlistService allowlist, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            if (request.Emails is not { Count: > 0 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["emails"] = ["At least one email is required."] });

            var enabled = await allowlist.IsEnforcedAsync(ct);
            var added = new List<string>();
            foreach (var raw in request.Emails.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(raw) || !raw.Contains('@', StringComparison.Ordinal))
                    continue;
                var email = raw.Trim();
                var normalized = MailAccount.NormalizeEmailAddress(email);
                if (await db.AllowlistedEmails.AnyAsync(item => item.NormalizedEmail == normalized, ct))
                    continue;
                db.AllowlistedEmails.Add(new AllowlistedEmail { Id = Guid.NewGuid(), Email = email, NormalizedEmail = normalized, AddedAt = DateTime.UtcNow });
                added.Add(email);

                if (!enabled)
                    continue;
                // Restore access immediately for an account that was disabled specifically for falling off the allowlist.
                var account = await db.MailAccounts.SingleOrDefaultAsync(item => item.NormalizedEmailAddress == normalized, ct);
                if (account is { Status: MailAccountStatus.Disabled, AccessRevokedAt: not null })
                {
                    account.Status = MailAccountStatus.Active;
                    account.AccessRevokedAt = null;
                    account.UpdatedAt = DateTime.UtcNow;
                    await audit.WriteAsync(account.Id, AuditActions.MailAccountAccessRestored, "MailAccount", account.Id.ToString(), null, correlation.CorrelationId, ct);
                }
            }

            await db.SaveChangesAsync(ct);
            if (added.Count > 0)
                await audit.WriteAsync(null, AuditActions.AllowlistEmailAdded, "AllowlistedEmail", null, new { emails = added }, correlation.CorrelationId, ct);
            return Results.Ok(new AddAllowlistEmailsResponse(added));
        }).WithName("AddAllowlistEmails").WithSummary("Add emails to the allowlist")
            .WithDescription("Requires the X-Management-Key header. Duplicates and invalid values are skipped. When enforcement is on, re-enables a mailbox that was disabled for being removed.")
            .Produces<AddAllowlistEmailsResponse>().ProducesValidationProblem().ProblemCodes(401, "management_unauthorized");

        whitelist.MapDelete("/emails/{email}", async (string email, AppDbContext db, IEmailAllowlistService allowlist, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var normalized = MailAccount.NormalizeEmailAddress(email);
            var entry = await db.AllowlistedEmails.SingleOrDefaultAsync(item => item.NormalizedEmail == normalized, ct);
            if (entry is null)
                return Results.NotFound();
            db.AllowlistedEmails.Remove(entry);

            var enabled = await allowlist.IsEnforcedAsync(ct);
            if (enabled)
            {
                // Cut off access immediately instead of waiting for the next reconciliation pass.
                var account = await db.MailAccounts.SingleOrDefaultAsync(item => item.NormalizedEmailAddress == normalized, ct);
                if (account is { Status: not MailAccountStatus.Disabled })
                {
                    account.Status = MailAccountStatus.Disabled;
                    account.AccessRevokedAt = DateTime.UtcNow;
                    account.UpdatedAt = DateTime.UtcNow;
                    await audit.WriteAsync(account.Id, AuditActions.MailAccountAccessRevoked, "MailAccount", account.Id.ToString(), null, correlation.CorrelationId, ct);
                }
            }

            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(null, AuditActions.AllowlistEmailRemoved, "AllowlistedEmail", entry.Id.ToString(), new { email = entry.Email }, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("RemoveAllowlistEmail").WithSummary("Remove an email from the allowlist")
            .WithDescription("Requires the X-Management-Key header. When enforcement is on, immediately disables that mailbox.")
            .Produces(204).Produces(404).ProblemCodes(401, "management_unauthorized");
    }
}
