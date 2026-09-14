using MailClient.Api.Auth;
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

public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/folders", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => Results.Ok(await db.MailFolders.Where(x => x.MailAccountId == current.MailAccountId).ToListAsync(ct))).WithName("ListFolders").WithSummary("List mailbox folders").Produces<List<MailFolder>>();
        api.MapPost("/folders/refresh", async (ICurrentMailAccount current, AccountConnectionService connector, AuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            var count = await connector.RefreshFoldersAsync(current.MailAccountId, ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.MailSyncRequested, "MailFolder", null,
                new Dictionary<string, string?> { ["folders"] = count.ToString() }, http.TraceIdentifier, ct);
            return Results.Accepted(value: new { folders = count });
        }).WithName("RefreshFolders").WithSummary("Refresh mailbox folders").Produces(202).ProducesProblem(409).ProducesProblem(502);
        api.MapPost("/folders/{id:guid}/sync", async (Guid id, ICurrentMailAccount current, MailOperationsService service, InitialSyncQueue queue, AuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            if (!await service.OwnsFolderAsync(current.MailAccountId, id, ct)) return Results.NotFound();
            await queue.EnqueueAsync(SyncRequest.Folder(current.MailAccountId, id), ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.MailSyncRequested, "MailFolder", id.ToString(), null, http.TraceIdentifier, ct);
            return Results.Accepted();
        }).WithName("SyncFolder").WithSummary("Request folder synchronization").Produces(202).Produces(404);
    }
}
