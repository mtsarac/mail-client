using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Observability;

namespace MailClient.Api.Endpoints;

public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        var oauth = app.MapGroup("/api/accounts/oauth").WithTags("OAuth");

        oauth.MapPost("/{provider}/start", async (string provider, OAuthStartRequest request, OAuthConnectionService service, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            if (!TryParseProvider(provider, out var mailProvider))
                return Results.Problem(statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "oauth_provider_not_configured" });
            var result = await service.StartAsync(mailProvider, request, ct);
            await audit.WriteAsync(null, AuditActions.MailAccountOAuthStarted, "MailAccount", null,
                new Dictionary<string, string?> { ["provider"] = mailProvider.ToString() }, correlation.CorrelationId, ct);
            return Results.Ok(result);
        }).AllowAnonymous().WithName("StartOAuthAuthorization").WithSummary("Start OAuth authorization (step 1)").WithDescription("Validates provider configuration and returns a provider authorization URL with an opaque protected state. The state is Data-Protection encrypted and time limited.").Produces<OAuthStartResponse>().ProblemCodes(400, "invalid_email").ProblemCodes(403, "email_not_allowlisted", "provider_disabled", "provider_new_accounts_disabled", "provider_existing_accounts_disabled", "authentication_method_disabled").ProblemCodes(422, "oauth_provider_not_configured", "oauth_redirect_uri_invalid");

        oauth.MapPost("/{provider}/complete", async (string provider, OAuthCompleteRequest request, OAuthConnectionService service, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            if (!TryParseProvider(provider, out var mailProvider))
                return Results.Problem(statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "oauth_provider_not_configured" });
            var tokens = await service.CompleteAsync(mailProvider, request, ct);
            await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountConnected, "MailAccount", tokens.MailAccountId.ToString(),
                new Dictionary<string, string?> { ["provider"] = mailProvider.ToString(), ["authentication"] = AuthenticationMethod.OAuth2.ToString() }, correlation.CorrelationId, ct);
            return Results.Ok(tokens);
        }).AllowAnonymous().WithName("CompleteOAuthAuthorization").WithSummary("Complete OAuth authorization (step 2)").WithDescription("Send the `state` from /start and the `code` the provider returned on the redirect. Exchanges the returned authorization code using the protected state, validates the mailbox over XOAUTH2, and returns the application TokenResponse. Provider tokens are never returned.").Produces<TokenResponse>().ProblemCodes(403, "email_not_allowlisted", "provider_disabled", "authentication_method_disabled").ProblemCodes(409, "mail_account_needs_reauthentication").ProblemCodes(422, "oauth_provider_not_configured", "oauth_state_invalid", "oauth_code_exchange_failed");

        static bool TryParseProvider(string provider, out MailProvider mailProvider)
        {
            if (Enum.TryParse<MailProvider>(provider, true, out mailProvider) && mailProvider is MailProvider.Google or MailProvider.Microsoft)
                return true;
            mailProvider = default;
            return false;
        }
    }
}
