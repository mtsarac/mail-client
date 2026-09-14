using MailClient.Api.Auth;
using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Domain;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Discovery;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var accounts = app.MapGroup("/api/accounts").WithTags("Accounts");
        accounts.MapPost("/discover", async (DiscoverRequest request, MailServerDiscoveryService discovery, DiscoveryStateStore states, AuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@')) return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Valid email is required."] });
            var candidate = await discovery.DiscoverAsync(request.Email.Trim(), ct);
            if (candidate is null)
            {
                await audit.WriteAsync(null, AuditActions.MailAccountDiscoveryFailed, "MailAccount", null,
                    new Dictionary<string, string?> { ["email"] = request.Email.Trim() }, http.TraceIdentifier, ct);
                return Results.Problem(title: "Mail server discovery failed.", statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "mail_discovery_failed", ["manualSetupAvailable"] = true });
            }

            var id = states.Store(request.Email.Trim(), candidate, TimeSpan.FromMinutes(10));
            await audit.WriteAsync(null, AuditActions.MailAccountDiscoverySucceeded, "MailAccount", null,
                new Dictionary<string, string?> { ["email"] = request.Email.Trim(), ["provider"] = candidate.Provider.ToString(), ["source"] = candidate.Source.ToString() }, http.TraceIdentifier, ct);
            return Results.Ok(new DiscoverResponse(id, request.Email.Trim(), candidate.Provider, candidate.AuthenticationMethods, true));
        }).AllowAnonymous().WithName("DiscoverMailAccount").WithSummary("Discover mail servers").WithDescription("Runs known provider, DNS SRV, autoconfig, Autodiscover, then safe heuristics. Manual setup is fallback only.").Produces<DiscoverResponse>().ProducesProblem(422).ProducesValidationProblem();
        accounts.MapPost("/connect", async (ConnectRequest request, DiscoveryStateStore states, AccountConnectionService service, AuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            if (states.Get(request.DiscoveryId) is not { } state)
                return Results.Problem(statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "discovery_expired" });
            try
            {
                var tokens = await service.ConnectAsync(state, request.Authentication, request.DeviceIdentifier, ct);
                states.Consume(request.DiscoveryId);
                await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountConnected, "MailAccount", tokens.MailAccountId.ToString(),
                    new Dictionary<string, string?> { ["provider"] = state.Candidate.Provider.ToString() }, http.TraceIdentifier, ct);
                return Results.Ok(tokens);
            }
            catch (InvalidOperationException ex) when (ex.Message == "mail_authentication_failed")
            {
                await audit.WriteAsync(null, AuditActions.MailAccountAuthenticationFailed, "MailAccount", null,
                    new Dictionary<string, string?> { ["email"] = state.Email }, http.TraceIdentifier, ct);
                throw;
            }
        }).AllowAnonymous().WithName("ConnectDiscoveredAccount").WithSummary("Connect discovered mailbox").Produces<TokenResponse>().ProducesProblem(422);
        accounts.MapPost("/connect-manual", async (ManualConnectRequest request, AccountConnectionService service, AuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            await audit.WriteAsync(null, AuditActions.MailAccountManualSetupAttempted, "MailAccount", null,
                new Dictionary<string, string?> { ["email"] = request.Email, ["imapHost"] = request.Imap.Host, ["smtpHost"] = request.Smtp.Host }, http.TraceIdentifier, ct);
            var tokens = await service.ConnectManualAsync(request, ct);
            await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountManualSetupSucceeded, "MailAccount", tokens.MailAccountId.ToString(), null, http.TraceIdentifier, ct);
            return Results.Ok(tokens);
        }).AllowAnonymous().WithName("ConnectManualAccount").WithSummary("Connect mailbox with manual server settings").WithDescription("Fallback only. Host, IP, TLS, IMAP, SMTP, and credentials receive the same validation as automatic discovery.").Produces<TokenResponse>().ProducesProblem(422);

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/account", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => await db.MailAccounts.Where(x => x.Id == current.MailAccountId).Select(x => new AccountResponse(x.Id, x.EmailAddress, x.DisplayName, x.Provider, x.Status)).SingleOrDefaultAsync(ct) is { } account ? Results.Ok(account) : Results.NotFound()).WithName("GetCurrentAccount").WithSummary("Get current mailbox account").Produces<AccountResponse>().Produces(404);
        api.MapDelete("/account", async (ICurrentMailAccount current, AppDbContext db, LocalAttachmentStorage storage, ILogger<Program> logger, CancellationToken ct) =>
        {
            var account = await db.MailAccounts.FindAsync([current.MailAccountId], ct);
            if (account is null) return Results.NotFound();
            db.Remove(account);
            await db.SaveChangesAsync(ct);
            try
            {
                await storage.DeleteAccountAsync(current.MailAccountId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Attachment cleanup failed after mailbox deletion.");
            }
            return Results.NoContent();
        }).WithName("DeleteCurrentAccount").WithSummary("Delete mailbox and cached data").Produces(204).Produces(404);
    }
}
