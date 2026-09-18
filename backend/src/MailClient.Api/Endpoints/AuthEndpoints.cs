using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Application.Authentication;
using MailClient.Domain;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Authentication");
        auth.MapPost("/refresh", async (RefreshRequest request, MailSessionService sessions, IJwtTokenIssuer tokens, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var rotation = await sessions.RotateAsync(request.RefreshToken, ct);
            if (rotation is null) return Results.Problem(statusCode: 401, extensions: new Dictionary<string, object?> { ["code"] = "invalid_refresh_token" });
            var status = await db.MailAccounts.AsNoTracking()
                .Where(account => account.Id == rotation.Value.Session.MailAccountId)
                .Select(account => (MailAccountStatus?)account.Status)
                .SingleOrDefaultAsync(ct);
            if (status is null or MailAccountStatus.Disabled)
            {
                // Rotation already revoked the presented token and minted a replacement session; a disabled
                // or deleted account must not keep either one alive, so revoke the replacement too.
                await sessions.RevokeAsync(rotation.Value.Token, ct);
                return Results.Problem(statusCode: 401, extensions: new Dictionary<string, object?> { ["code"] = "invalid_refresh_token" });
            }

            var access = tokens.Issue(rotation.Value.Session.MailAccountId);
            await audit.WriteAsync(rotation.Value.Session.MailAccountId, AuditActions.MailSessionRefreshed, "MailSession", rotation.Value.Session.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(new TokenResponse(access.Token, rotation.Value.Token, rotation.Value.Session.MailAccountId, access.ExpiresAt));
        }).AllowAnonymous().WithName("RefreshSession").WithSummary("Rotate refresh session").Produces<TokenResponse>().ProducesProblem(401);
        auth.MapPost("/logout", async (LogoutRequest request, MailSessionService sessions, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            if (!await sessions.RevokeAsync(request.RefreshToken, ct))
                return Results.Problem(statusCode: 401, extensions: new Dictionary<string, object?> { ["code"] = "session_revoked" });
            await audit.WriteAsync(null, AuditActions.MailSessionRevoked, "MailSession", null, null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).AllowAnonymous().WithName("LogoutSession").WithSummary("Revoke client session").Produces(204).ProducesProblem(401);
    }
}
