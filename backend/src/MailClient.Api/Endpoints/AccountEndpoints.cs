using MailClient.Api.Auth;
using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
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

public sealed record AccountSignatureRequest(string? Signature);
public sealed record AccountSyncScopeRequest(MailClient.Domain.Enums.FolderSyncScope Scope, IReadOnlyList<Guid>? FolderIds);
public sealed record AccountSyncScopeResponse(MailClient.Domain.Enums.FolderSyncScope Scope, IReadOnlyList<Guid> SyncedFolderIds);
public sealed record AccountNotificationSettingsRequest(bool Enabled, bool InboxOnly, MailClient.Domain.Enums.NotificationPrivacy Privacy);
public sealed record AccountNotificationSettingsResponse(bool Enabled, bool InboxOnly, MailClient.Domain.Enums.NotificationPrivacy Privacy, bool PreviewsAllowedByServer);

public sealed record FolderSyncStatusResponse(
    Guid FolderId,
    string FolderName,
    MailClient.Domain.Enums.MailFolderType FolderType,
    bool BackfillComplete,
    DateTime? LastSuccessfulSyncAt,
    DateTime? LastFailureAt,
    MailClient.Domain.Enums.SyncFailureCategory? LastFailureCategory,
    int ConsecutiveFailures);

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
        }).AllowAnonymous().WithName("DiscoverMailAccount").WithSummary("Discover mail servers").WithDescription("Runs known provider, DNS SRV, autoconfig, Autodiscover, then safe heuristics. Manual setup is fallback only.").Produces<DiscoverResponse>().ProblemCodes(422, "mail_discovery_failed").ProducesValidationProblem();
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
        }).AllowAnonymous().WithName("ConnectDiscoveredAccount").WithSummary("Connect a discovered mailbox (create only)")
            .WithDescription("Creates a NEW mailbox account from a discoveryId (valid ~10 min) and returns tokens. Fails with mail_account_already_exists if the mailbox is registered; use /login for that.")
            .Produces<TokenResponse>().ProblemCodes(401, "mail_authentication_failed")
            .ProblemCodes(403, "email_not_allowlisted", "provider_disabled", "provider_new_accounts_disabled", "authentication_method_disabled")
            .ProblemCodes(409, "mail_account_already_exists").ProblemCodes(422, "discovery_expired", "mail_server_unsafe", "unsupported_authentication_method");
        accounts.MapPost("/connect-manual", async (ManualConnectRequest request, AccountConnectionService service, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            await audit.WriteAsync(null, AuditActions.MailAccountManualSetupAttempted, "MailAccount", null,
                new Dictionary<string, string?> { ["email"] = request.Email, ["imapHost"] = request.Imap.Host, ["smtpHost"] = request.Smtp.Host }, correlation.CorrelationId, ct);
            var tokens = await service.ConnectManualAsync(request, ct);
            await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountManualSetupSucceeded, "MailAccount", tokens.MailAccountId.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(tokens);
        }).AllowAnonymous().WithName("ConnectManualAccount").WithSummary("Connect a mailbox with manual server settings (create only)").WithDescription("Fallback only; creates a NEW account. Host, IP, TLS, IMAP, SMTP, and credentials receive the same validation as automatic discovery.")
            .Produces<TokenResponse>().ProblemCodes(400, "manual_setup_invalid", "invalid_email").ProblemCodes(401, "mail_authentication_failed", "mail_smtp_authentication_failed")
            .ProblemCodes(403, "email_not_allowlisted", "provider_disabled", "provider_new_accounts_disabled", "authentication_method_disabled")
            .ProblemCodes(409, "mail_account_already_exists").ProblemCodes(422, "mail_server_unsafe", "unsupported_authentication_method");
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
            .Produces<TokenResponse>().ProblemCodes(401, "mail_authentication_failed")
            .ProblemCodes(403, "mail_account_disabled", "email_not_allowlisted", "provider_disabled", "provider_existing_accounts_disabled", "authentication_method_disabled")
            .ProblemCodes(404, "mail_account_not_found");

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/account", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => await db.MailAccounts.Where(x => x.Id == current.MailAccountId).Select(x => new AccountResponse(x.Id, x.EmailAddress, x.DisplayName, x.Provider, x.Status, x.Signature)).SingleOrDefaultAsync(ct) is { } account ? Results.Ok(account) : Results.NotFound()).WithTags("Account").WithName("GetCurrentAccount").WithSummary("Get current mailbox account").Produces<AccountResponse>().Produces(404);
        api.MapGet("/account/sync-status", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var statuses = await db.MailFolders.AsNoTracking()
                .Where(f => f.MailAccountId == current.MailAccountId && f.IsAvailable)
                .Select(f => new FolderSyncStatusResponse(
                    f.Id, f.Name, f.FolderType, f.SyncState != null && f.SyncState.BackfillNextUid == 0,
                    f.SyncState == null ? null : f.SyncState.LastSuccessfulSyncAt,
                    f.SyncState == null ? null : f.SyncState.LastFailureAt,
                    f.SyncState == null ? null : f.SyncState.LastFailureCategory,
                    f.SyncState == null ? 0 : f.SyncState.ConsecutiveFailures))
                .ToListAsync(ct);
            return Results.Ok(statuses);
        }).WithTags("Account").WithName("GetAccountSyncStatus").WithSummary("Per-folder sync and backfill status for the current mailbox")
            .WithDescription("One entry per available folder. `BackfillComplete` is false before the first sync and while older mail history is still being imported; search results may be incomplete.")
            .Produces<IReadOnlyList<FolderSyncStatusResponse>>();
        api.MapGet("/account/sync-scope", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
            await db.MailAccounts.AsNoTracking().Where(x => x.Id == current.MailAccountId).Select(x => (MailClient.Domain.Enums.FolderSyncScope?)x.FolderSyncScope).SingleOrDefaultAsync(ct) is { } scope
                ? Results.Ok(new AccountSyncScopeResponse(scope, await SyncedFolderIdsAsync(db, current.MailAccountId, ct)))
                : Results.NotFound()
        ).WithTags("Account").WithName("GetAccountSyncScope").WithSummary("Which folders background sync follows for the current mailbox")
            .WithDescription("`SyncedFolderIds` lists available folders that periodic background sync currently covers.")
            .Produces<AccountSyncScopeResponse>().Produces(404);
        api.MapPut("/account/sync-scope", UpdateSyncScopeAsync).WithTags("Account").WithName("UpdateAccountSyncScope")
            .WithSummary("Choose which folders background sync follows for the current mailbox")
            .WithDescription("scope: InboxAndSent (default), AllFolders, or SelectedFolders. folderIds is required (1+ ids of this mailbox's available folders) for SelectedFolders and must be omitted otherwise. Folders discovered later follow the scope: AllFolders includes them, SelectedFolders does not. Folders opened by the user are still synced on demand; newly included folders are picked up by the next periodic sync pass.")
            .Produces<AccountSyncScopeResponse>().ProducesValidationProblem().Produces(404);
        api.MapGet("/account/notification-settings", async (ICurrentMailAccount current, AppDbContext db, IRuntimeSettingsStore runtime, CancellationToken ct) =>
        {
            var account = await db.MailAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == current.MailAccountId, ct);
            return account is null ? Results.NotFound() : Results.Ok(await NotificationSettingsAsync(account, runtime, ct));
        }).WithTags("Account").WithName("GetAccountNotificationSettings").WithSummary("Mail notification preferences of the current mailbox")
            .WithDescription("Shared by every device signed into this mailbox. `PreviewsAllowedByServer` is false when the server operator disabled mail previews; pushes are then sent as Private regardless of `Privacy`.")
            .Produces<AccountNotificationSettingsResponse>().Produces(404);
        api.MapPut("/account/notification-settings", async (AccountNotificationSettingsRequest request, ICurrentMailAccount current, AppDbContext db, IRuntimeSettingsStore runtime, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(request.Privacy))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["privacy"] = ["Unknown notification privacy."] });
            var account = await db.MailAccounts.SingleOrDefaultAsync(x => x.Id == current.MailAccountId, ct);
            if (account is null) return Results.NotFound();
            account.NotificationsEnabled = request.Enabled;
            account.NotifyInboxOnly = request.InboxOnly;
            account.NotificationPrivacy = request.Privacy;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await NotificationSettingsAsync(account, runtime, ct));
        }).WithTags("Account").WithName("UpdateAccountNotificationSettings").WithSummary("Change mail notification preferences of the current mailbox")
            .WithDescription("enabled: send new-mail and snooze wake-up pushes for this mailbox. inboxOnly: notify only for Inbox (default) or for every synced folder except Sent, Drafts, Trash and Junk. privacy: Full (sender, subject, short preview), Limited (sender, subject; default) or Private (generic text only). Device registration is unaffected.")
            .Produces<AccountNotificationSettingsResponse>().ProducesValidationProblem().Produces(404);
        api.MapPost("/account/reconnect", async (AccountReconnectRequest request, ICurrentMailAccount current, AccountConnectionService service, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var account = await service.ReconnectAsync(current.MailAccountId, request, ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.MailAccountConnected, "MailAccount", current.MailAccountId.ToString(),
                new Dictionary<string, string?> { ["provider"] = account.Provider.ToString(), ["authentication"] = account.AuthenticationMethod.ToString() }, correlation.CorrelationId, ct);
            return Results.Ok(new AccountResponse(account.Id, account.EmailAddress, account.DisplayName, account.Provider, account.Status, account.Signature));
        }).WithTags("Account").WithName("ReconnectCurrentAccount").WithSummary("Update credentials or server settings for the signed-in mailbox")
            .WithDescription("Changes credentials and/or IMAP/SMTP settings of the authenticated mailbox only. Anonymous connect endpoints only create new accounts.")
            .Produces<AccountResponse>().ProblemCodes(401, "mail_authentication_failed")
            .ProblemCodes(403, "mail_account_disabled", "provider_disabled", "provider_existing_accounts_disabled", "authentication_method_disabled")
            .ProblemCodes(404, "mail_account_not_found").ProblemCodes(422, "mail_server_unsafe", "unsupported_authentication_method");
        api.MapDelete("/account", async (ICurrentMailAccount current, AccountDeletionService deletion, CancellationToken ct) =>
            await deletion.DeleteAsync(current.MailAccountId, ct) ? Results.NoContent() : Results.NotFound()
        ).WithTags("Account").WithName("DeleteCurrentAccount").WithSummary("Delete mailbox and cached data").WithDescription("Permanently removes the account and all cached mail, folders, sessions and devices from this server. The remote mailbox is untouched. Cannot be undone.").Produces(204).Produces(404);
        api.MapGet("/account/sessions", async (ICurrentMailAccount current, MailSessionService sessions, CancellationToken ct) =>
        {
            var active = await sessions.ListActiveAsync(current.MailAccountId, ct);
            return Results.Ok(active.Select(x => new MailSessionResponse(x.Id, x.DeviceIdentifier, x.CreatedAt, x.LastUsedAt, x.ExpiresAt)));
        }).WithTags("Account").WithName("ListAccountSessions").WithSummary("List active sessions (signed-in devices) for the current mailbox").Produces<IEnumerable<MailSessionResponse>>();
        api.MapDelete("/account/sessions/{sessionId:guid}", async (Guid sessionId, ICurrentMailAccount current, MailSessionService sessions, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
            await sessions.RevokeAsync(current.MailAccountId, sessionId, ct)
                ? await Audited(audit, current.MailAccountId, sessionId, correlation, ct)
                : Results.NotFound()
        ).WithTags("Account").WithName("RevokeAccountSession").WithSummary("Sign out a device by revoking its session").Produces(204).Produces(404);
        api.MapPut("/account/signature", async (AccountSignatureRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var account = await db.MailAccounts.SingleOrDefaultAsync(x => x.Id == current.MailAccountId, ct);
            if (account is null) return Results.NotFound();
            var signature = request.Signature?.Trim();
            account.Signature = signature is { Length: 0 } ? null : signature;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Account").WithName("UpdateAccountSignature").WithSummary("Set or clear the signature appended to outgoing mail")
            .WithDescription("Blank/whitespace-only clears the signature. Synced across every device signed into this account.")
            .Produces(204).Produces(404);
    }

    private static async Task<IResult> UpdateSyncScopeAsync(AccountSyncScopeRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Scope))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["scope"] = ["Unknown sync scope."] });
        var selected = request.FolderIds?.Distinct().ToHashSet() ?? [];
        if (request.Scope == MailClient.Domain.Enums.FolderSyncScope.SelectedFolders && selected.Count == 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["folderIds"] = ["At least one folder id is required for SelectedFolders."] });
        if (request.Scope != MailClient.Domain.Enums.FolderSyncScope.SelectedFolders && request.FolderIds is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["folderIds"] = ["folderIds is only allowed with SelectedFolders."] });
        var account = await db.MailAccounts.SingleOrDefaultAsync(x => x.Id == current.MailAccountId, ct);
        if (account is null) return Results.NotFound();
        var folders = await db.MailFolders.Where(x => x.MailAccountId == current.MailAccountId).ToListAsync(ct);
        if (!selected.IsSubsetOf(folders.Where(x => x.IsAvailable).Select(x => x.Id)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["folderIds"] = ["Every folder id must belong to an available folder of this mailbox."] });
        account.FolderSyncScope = request.Scope;
        account.UpdatedAt = DateTime.UtcNow;
        foreach (var folder in folders)
            folder.IsSyncEnabled = request.Scope == MailClient.Domain.Enums.FolderSyncScope.SelectedFolders
                ? selected.Contains(folder.Id)
                : MailClient.Domain.Entities.MailFolder.SyncedByDefault(request.Scope, folder.FolderType);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new AccountSyncScopeResponse(request.Scope, await SyncedFolderIdsAsync(db, current.MailAccountId, ct)));
    }

    private static async Task<IReadOnlyList<Guid>> SyncedFolderIdsAsync(AppDbContext db, Guid accountId, CancellationToken ct) =>
        await db.MailFolders.AsNoTracking().Where(x => x.MailAccountId == accountId && x.IsSyncEnabled && x.IsAvailable).Select(x => x.Id).ToListAsync(ct);

    private static async Task<AccountNotificationSettingsResponse> NotificationSettingsAsync(MailClient.Domain.Entities.MailAccount account, IRuntimeSettingsStore runtime, CancellationToken ct) =>
        new(account.NotificationsEnabled, account.NotifyInboxOnly, account.NotificationPrivacy,
            (await runtime.GetAsync(ct)).Settings.Push.IncludeMailPreview);

    private static async Task<IResult> Audited(AuditLogger audit, Guid accountId, Guid sessionId, CorrelationContext correlation, CancellationToken ct)
    {
        await audit.WriteAsync(accountId, AuditActions.MailSessionRevoked, "MailSession", sessionId.ToString(), null, correlation.CorrelationId, ct);
        return Results.NoContent();
    }
}
