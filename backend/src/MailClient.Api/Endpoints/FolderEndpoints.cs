using MailClient.Api.Auth;
using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Application.Sync;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record RefreshFoldersResponse(int Folders);

/// <summary>Documented shape of a folder item; the response may carry additional null/empty navigation fields.</summary>
public sealed record MailFolderResponse(Guid Id, Guid MailAccountId, string Name, string FullName, MailClient.Domain.Enums.MailFolderType FolderType, uint UidValidity, bool IsSyncEnabled, bool IsAvailable);

public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Folders");
        api.MapGet("/folders", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => Results.Ok(await db.MailFolders.Where(x => x.MailAccountId == current.MailAccountId).ToListAsync(ct))).WithName("ListFolders").WithSummary("List mailbox folders").WithDescription("Cached folders of the current mailbox. Use `id` as folderId elsewhere. Empty until folders are refreshed.").Produces<List<MailFolderResponse>>();
        api.MapPost("/folders/refresh", async (ICurrentMailAccount current, AccountConnectionService connector, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var count = await connector.RefreshFoldersAsync(current.MailAccountId, ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.MailSyncRequested, "MailFolder", null,
                new Dictionary<string, string?> { ["folders"] = count.ToString() }, correlation.CorrelationId, ct);
            return Results.Accepted(value: new RefreshFoldersResponse(count));
        }).WithName("RefreshFolders").WithSummary("Refresh mailbox folders").WithDescription("Re-reads the folder list from the mail server and returns how many folders were found.").Produces<RefreshFoldersResponse>(202)
            .ProblemCodes(403, "mail_account_disabled").ProblemCodes(409, "mail_account_needs_reauthentication", "credential_missing")
            .ProblemCodes(502, "mail_provider_unavailable", "mail_server_unreachable", "mail_tls_failed");
        api.MapPost("/folders/{id:guid}/sync", async (Guid id, ICurrentMailAccount current, MailFolderAccessService service, ISyncScheduler scheduler, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var folder = await service.GetFolderSyncAvailabilityAsync(current.MailAccountId, id, ct);
            if (folder is null) return Results.NotFound();
            if (!folder.IsAvailable)
                return Results.Problem(statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "mail_folder_unavailable" });
            try
            {
                await scheduler.ScheduleFolderAsync(current.MailAccountId, id, SyncOrigin.UserRequested, ct);
            }
            catch (SyncQueueFullException)
            {
                return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "sync_queue_full" });
            }
            await audit.WriteAsync(current.MailAccountId, AuditActions.MailSyncRequested, "MailFolder", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Accepted();
        }).WithName("SyncFolder").WithSummary("Request folder synchronization").WithDescription("Asynchronous: returns 202 Accepted once the sync is queued, not when it finishes. Poll GET /api/mails to see new mail.")
            .Produces(202).Produces(404).ProblemCodes(409, "mail_folder_unavailable").ProblemCodes(503, "sync_queue_full");
    }
}
