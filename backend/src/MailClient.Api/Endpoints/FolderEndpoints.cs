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
public sealed record MailFolderResponse(Guid Id, Guid MailAccountId, string Name, string FullName, MailClient.Domain.Enums.MailFolderType FolderType, uint UidValidity, bool IsSyncEnabled, bool IsAvailable, int UnreadCount = 0, int TotalCount = 0, string? Delimiter = null, Guid? ParentId = null);
public sealed record CreateFolderRequest(string? Name, Guid? ParentId);
public sealed record RenameFolderRequest(string? Name);

public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Folders");
        api.MapGet("/folders", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => Results.Ok(await ListAsync(db, current.MailAccountId, ct))).WithName("ListFolders").WithSummary("List mailbox folders").WithDescription("Cached folders of the current mailbox. Use `id` as folderId elsewhere. `delimiter` is the IMAP hierarchy separator and `parentId` the folder directly above (null at top level). Empty until folders are refreshed.").Produces<List<MailFolderResponse>>();
        api.MapPost("/folders", async (CreateFolderRequest request, ICurrentMailAccount current, FolderManagementService folders, AppDbContext db, CorrelationContext correlation, CancellationToken ct) =>
        {
            var result = await folders.CreateAsync(current.MailAccountId, request.ParentId, request.Name, correlation.CorrelationId, ct);
            return result.Error is { } error
                ? FolderProblem(error, correlation.CorrelationId)
                : Results.Created($"/api/folders/{result.Folder!.Id}", await DescribeAsync(db, current.MailAccountId, result.Folder.Id, ct));
        }).WithName("CreateFolder").WithSummary("Create a mailbox folder")
            .WithDescription("Creates the folder on the mail server under parentId (or at the top level of the personal namespace), then caches it. The name must not contain the hierarchy delimiter.")
            .Produces<MailFolderResponse>(201).ProblemCodes(400, "invalid_folder_name").ProblemCodes(404, "mail_folder_not_found")
            .ProblemCodes(409, "mail_folder_exists", "mail_account_needs_reauthentication").ProblemCodes(422, "mail_folder_rejected")
            .ProblemCodes(502, "mail_provider_unavailable", "mail_server_unreachable", "mail_tls_failed");
        api.MapPatch("/folders/{id:guid}", async (Guid id, RenameFolderRequest request, ICurrentMailAccount current, FolderManagementService folders, AppDbContext db, CorrelationContext correlation, CancellationToken ct) =>
        {
            var result = await folders.RenameAsync(current.MailAccountId, id, request.Name, correlation.CorrelationId, ct);
            return result.Error is { } error
                ? FolderProblem(error, correlation.CorrelationId)
                : Results.Ok(await DescribeAsync(db, current.MailAccountId, id, ct));
        }).WithName("RenameFolder").WithSummary("Rename a custom folder")
            .WithDescription("Renames the folder in place on the mail server. Ids are kept; child folders get their new fullName. Only Custom folders can be renamed.")
            .Produces<MailFolderResponse>().ProblemCodes(400, "invalid_folder_name").ProblemCodes(404, "mail_folder_not_found")
            .ProblemCodes(409, "mail_folder_exists", "mail_account_needs_reauthentication").ProblemCodes(422, "mail_folder_protected", "mail_folder_rejected")
            .ProblemCodes(502, "mail_provider_unavailable", "mail_server_unreachable", "mail_tls_failed");
        api.MapDelete("/folders/{id:guid}", async (Guid id, ICurrentMailAccount current, FolderManagementService folders, CorrelationContext correlation, CancellationToken ct) =>
            await folders.DeleteAsync(current.MailAccountId, id, correlation.CorrelationId, ct) is { } error
                ? FolderProblem(error, correlation.CorrelationId)
                : Results.NoContent())
            .WithName("DeleteFolder").WithSummary("Delete an empty custom folder")
            .WithDescription("Deletes the folder on the mail server. Refused while it has child folders or still contains messages on the server, so mail is never deleted implicitly. Only Custom folders can be deleted.")
            .Produces(204).ProblemCodes(404, "mail_folder_not_found")
            .ProblemCodes(409, "mail_folder_has_children", "mail_folder_not_empty", "mail_account_needs_reauthentication").ProblemCodes(422, "mail_folder_protected", "mail_folder_rejected")
            .ProblemCodes(502, "mail_provider_unavailable", "mail_server_unreachable", "mail_tls_failed");
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
                var jobId = await scheduler.ScheduleUserFolderJobAsync(current.MailAccountId, id, ct);
                await audit.WriteAsync(current.MailAccountId, AuditActions.MailSyncRequested, "MailFolder", id.ToString(), null, correlation.CorrelationId, ct);
                return Results.Accepted($"/api/folders/sync-jobs/{jobId}", new SyncJobStatus(jobId, "queued", null));
            }
            catch (SyncQueueFullException)
            {
                return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "sync_queue_full" });
            }
        }).WithName("SyncFolder").WithSummary("Request folder synchronization").WithDescription("Returns 202 and a jobId. Poll GET /api/folders/sync-jobs/{jobId}; read cached mail only after status is succeeded.")
            .Produces<SyncJobStatus>(202).Produces(404).ProblemCodes(409, "mail_folder_unavailable").ProblemCodes(503, "sync_queue_full");
        api.MapGet("/folders/sync-jobs/{jobId:guid}", (Guid jobId, ICurrentMailAccount current, ISyncScheduler scheduler) =>
            scheduler.GetJobStatus(current.MailAccountId, jobId) is { } job ? Results.Ok(job) : Results.NotFound())
            .WithName("GetFolderSyncJob").WithSummary("Get folder synchronization status")
            .WithDescription("Account-scoped status: queued, running, succeeded, or failed (with errorCode). Unknown or expired jobs return 404, including after server restart; clients must treat these as not completed. Terminal results remain in memory for up to one hour.")
            .Produces<SyncJobStatus>().Produces(404);
    }

    private static async Task<List<MailFolderResponse>> ListAsync(AppDbContext db, Guid accountId, CancellationToken ct)
    {
        var folders = await db.MailFolders.AsNoTracking().Where(x => x.MailAccountId == accountId).ToListAsync(ct);
        var counts = await db.Mails.AsNoTracking()
            .Where(m => m.MailAccountId == accountId && !m.Deleted)
            .GroupBy(m => m.MailFolderId)
            .Select(g => new { FolderId = g.Key, Unread = g.Count(m => !m.IsRead), Total = g.Count() })
            .ToDictionaryAsync(x => x.FolderId, ct);
        var parents = MailFolderHierarchy.ParentIds(folders);
        return folders.Select(x =>
        {
            var count = counts.GetValueOrDefault(x.Id);
            return new MailFolderResponse(x.Id, x.MailAccountId, x.Name, x.FullName, x.FolderType, x.UidValidity, x.IsSyncEnabled, x.IsAvailable,
                count?.Unread ?? 0, count?.Total ?? 0, x.Delimiter, parents[x.Id]);
        }).ToList();
    }

    private static async Task<MailFolderResponse> DescribeAsync(AppDbContext db, Guid accountId, Guid folderId, CancellationToken ct) =>
        (await ListAsync(db, accountId, ct)).Single(x => x.Id == folderId);

    private static IResult FolderProblem(string code, string correlationId) => Results.Problem(
        statusCode: code switch
        {
            "invalid_folder_name" => 400,
            "mail_folder_not_found" => 404,
            "mail_folder_exists" or "mail_folder_has_children" or "mail_folder_not_empty" => 409,
            _ => 422
        },
        extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = correlationId });
}
