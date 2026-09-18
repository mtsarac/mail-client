using MailClient.Api.Auth;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
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
        accounts.MapPost("/discover", async (DiscoverRequest request, MailServerDiscoveryService discovery, DiscoveryStateStore states, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@')) return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Valid email is required."] });
            var candidate = await discovery.DiscoverAsync(request.Email.Trim(), ct);
            if (candidate is null)
            {
                await audit.WriteAsync(null, AuditActions.MailAccountDiscoveryFailed, "MailAccount", null,
                    new Dictionary<string, string?> { ["email"] = request.Email.Trim() }, correlation.CorrelationId, ct);
                return Results.Problem(title: "Mail server discovery failed.", statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "mail_discovery_failed", ["manualSetupAvailable"] = true });
            }

            var id = states.Store(request.Email.Trim(), candidate, TimeSpan.FromMinutes(10));
            await audit.WriteAsync(null, AuditActions.MailAccountDiscoverySucceeded, "MailAccount", null,
                new Dictionary<string, string?> { ["email"] = request.Email.Trim(), ["provider"] = candidate.Provider.ToString(), ["source"] = candidate.Source.ToString() }, correlation.CorrelationId, ct);
            return Results.Ok(new DiscoverResponse(id, request.Email.Trim(), candidate.Provider, candidate.AuthenticationMethods, true));
        }).AllowAnonymous().WithName("DiscoverMailAccount").WithSummary("Discover mail servers").WithDescription("Runs known provider, DNS SRV, autoconfig, Autodiscover, then safe heuristics. Manual setup is fallback only.").Produces<DiscoverResponse>().ProducesProblem(422).ProducesValidationProblem();
        accounts.MapPost("/connect", async (ConnectRequest request, DiscoveryStateStore states, AccountConnectionService service, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            if (states.Get(request.DiscoveryId) is not { } state)
                return Results.Problem(statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "discovery_expired" });
            try
            {
                var tokens = await service.ConnectAsync(state, request.Authentication, request.DeviceIdentifier, ct);
                states.Consume(request.DiscoveryId);
                await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountConnected, "MailAccount", tokens.MailAccountId.ToString(),
                    new Dictionary<string, string?> { ["provider"] = state.Candidate.Provider.ToString() }, correlation.CorrelationId, ct);
                return Results.Ok(tokens);
            }
            catch (InvalidOperationException ex) when (ex.Message == "mail_authentication_failed")
            {
                await audit.WriteAsync(null, AuditActions.MailAccountAuthenticationFailed, "MailAccount", null,
                    new Dictionary<string, string?> { ["email"] = state.Email }, correlation.CorrelationId, ct);
                throw;
            }
        }).AllowAnonymous().WithName("ConnectDiscoveredAccount").WithSummary("Connect discovered mailbox").Produces<TokenResponse>().ProducesProblem(422);
        accounts.MapPost("/connect-manual", async (ManualConnectRequest request, AccountConnectionService service, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            await audit.WriteAsync(null, AuditActions.MailAccountManualSetupAttempted, "MailAccount", null,
                new Dictionary<string, string?> { ["email"] = request.Email, ["imapHost"] = request.Imap.Host, ["smtpHost"] = request.Smtp.Host }, correlation.CorrelationId, ct);
            var tokens = await service.ConnectManualAsync(request, ct);
            await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountManualSetupSucceeded, "MailAccount", tokens.MailAccountId.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(tokens);
        }).AllowAnonymous().WithName("ConnectManualAccount").WithSummary("Connect mailbox with manual server settings").WithDescription("Fallback only. Host, IP, TLS, IMAP, SMTP, and credentials receive the same validation as automatic discovery.").Produces<TokenResponse>().ProducesProblem(422);
        accounts.MapPost("/login", async (LoginRequest request, AccountConnectionService service, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            try
            {
                var tokens = await service.LoginAsync(request.Email, request.Password, request.DeviceIdentifier, ct);
                await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountLoggedIn, "MailAccount", tokens.MailAccountId.ToString(),
                    new Dictionary<string, string?> { ["deviceIdentifier"] = request.DeviceIdentifier }, correlation.CorrelationId, ct);
                return Results.Ok(tokens);
            }
            catch (InvalidOperationException ex) when (ex.Message == "mail_authentication_failed")
            {
                await audit.WriteAsync(null, AuditActions.MailAccountAuthenticationFailed, "MailAccount", null,
                    new Dictionary<string, string?> { ["email"] = request.Email }, correlation.CorrelationId, ct);
                throw;
            }
        }).AllowAnonymous().WithName("LoginMailAccount").WithSummary("Sign in to an existing mailbox from another device")
            .WithDescription("Verifies credentials against the account's stored server settings and issues a new session. Does not create or modify the account; use /connect for that.")
            .Produces<TokenResponse>().ProducesProblem(404).ProducesProblem(422);

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/account", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => await db.MailAccounts.Where(x => x.Id == current.MailAccountId).Select(x => new AccountResponse(x.Id, x.EmailAddress, x.DisplayName, x.Provider, x.Status)).SingleOrDefaultAsync(ct) is { } account ? Results.Ok(account) : Results.NotFound()).WithName("GetCurrentAccount").WithSummary("Get current mailbox account").Produces<AccountResponse>().Produces(404);
        api.MapPost("/account/reconnect", async (AccountReconnectRequest request, ICurrentMailAccount current, AccountConnectionService service, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var account = await service.ReconnectAsync(current.MailAccountId, request, ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.MailAccountConnected, "MailAccount", current.MailAccountId.ToString(),
                new Dictionary<string, string?> { ["provider"] = account.Provider.ToString(), ["authentication"] = account.AuthenticationMethod.ToString() }, correlation.CorrelationId, ct);
            return Results.Ok(new AccountResponse(account.Id, account.EmailAddress, account.DisplayName, account.Provider, account.Status));
        }).WithName("ReconnectCurrentAccount").WithSummary("Update credentials or server settings for the signed-in mailbox")
            .WithDescription("Account-scoped credential/server update. Anonymous connect endpoints only create new accounts.")
            .Produces<AccountResponse>().Produces(401).ProducesProblem(422);
        api.MapDelete("/account", async (ICurrentMailAccount current, AccountDeletionService deletion, CancellationToken ct) =>
            await deletion.DeleteAsync(current.MailAccountId, ct) ? Results.NoContent() : Results.NotFound()
        ).WithName("DeleteCurrentAccount").WithSummary("Delete mailbox and cached data").Produces(204).Produces(404);
        api.MapGet("/account/sessions", async (ICurrentMailAccount current, MailSessionService sessions, CancellationToken ct) =>
        {
            var active = await sessions.ListActiveAsync(current.MailAccountId, ct);
            return Results.Ok(active.Select(x => new MailSessionResponse(x.Id, x.DeviceIdentifier, x.CreatedAt, x.LastUsedAt, x.ExpiresAt)));
        }).WithName("ListAccountSessions").WithSummary("List active sessions (signed-in devices) for the current mailbox").Produces<IEnumerable<MailSessionResponse>>();
        api.MapDelete("/account/sessions/{sessionId:guid}", async (Guid sessionId, ICurrentMailAccount current, MailSessionService sessions, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
            await sessions.RevokeAsync(current.MailAccountId, sessionId, ct)
                ? await Audited(audit, current.MailAccountId, sessionId, correlation, ct)
                : Results.NotFound()
        ).WithName("RevokeAccountSession").WithSummary("Sign out a device by revoking its session").Produces(204).Produces(404);
    }

    private static async Task<IResult> Audited(AuditLogger audit, Guid accountId, Guid sessionId, CorrelationContext correlation, CancellationToken ct)
    {
        await audit.WriteAsync(accountId, AuditActions.MailSessionRevoked, "MailSession", sessionId.ToString(), null, correlation.CorrelationId, ct);
        return Results.NoContent();
    }
}
