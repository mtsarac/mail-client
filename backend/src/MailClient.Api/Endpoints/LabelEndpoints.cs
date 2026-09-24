using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record LabelRequest(string Name, int Color);
public sealed record LabelResponse(Guid Id, string Name, int Color, int SortOrder);
public sealed record LabelListResponse(IReadOnlyList<LabelResponse> Items);
public sealed record LabelAssignmentRequest(IReadOnlyList<Guid> MailIds, IReadOnlyList<Guid> LabelIds);

/// <summary>Mail id -&gt; label ids currently assigned to it, account-scoped.</summary>
public sealed record LabelAssignmentsResponse(IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Assignments);

public static class LabelEndpoints
{
    public static void MapLabelEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Labels");

        api.MapGet("/labels", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new LabelListResponse(await db.MailLabels.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .OrderBy(x => x.SortOrder)
                .Select(x => new LabelResponse(x.Id, x.Name, x.Color, x.SortOrder))
                .ToListAsync(ct))))
            .WithName("ListLabels").WithSummary("List labels for the current mailbox").Produces<LabelListResponse>();

        api.MapPost("/labels", async (LabelRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var name = request.Name.Trim();
            if (name.Length == 0 || name.Length > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Label name must be 1-100 characters."] });
            if (await NameTakenAsync(db, current.MailAccountId, name, null, ct))
                return Results.Problem(statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "label_name_taken" });
            var sortOrder = 1 + await db.MailLabels.Where(x => x.MailAccountId == current.MailAccountId).Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? 0;
            var label = new MailLabel { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, Name = name, Color = request.Color, SortOrder = sortOrder };
            db.MailLabels.Add(label);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.LabelCreated, "MailLabel", label.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/labels/{label.Id}", ToResponse(label));
        }).WithName("CreateLabel").WithSummary("Create a label").Produces<LabelResponse>(201).ProducesValidationProblem().ProblemCodes(409, "label_name_taken");

        api.MapPut("/labels/{id:guid}", async (Guid id, LabelRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var label = await db.MailLabels.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (label is null) return Results.NotFound();
            var name = request.Name.Trim();
            if (name.Length == 0 || name.Length > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Label name must be 1-100 characters."] });
            if (await NameTakenAsync(db, current.MailAccountId, name, id, ct))
                return Results.Problem(statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "label_name_taken" });
            label.Name = name;
            label.Color = request.Color;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.LabelUpdated, "MailLabel", label.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(ToResponse(label));
        }).WithName("UpdateLabel").WithSummary("Rename/recolor a label").Produces<LabelResponse>().Produces(404).ProducesValidationProblem().ProblemCodes(409, "label_name_taken");

        api.MapDelete("/labels/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var label = await db.MailLabels.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (label is null) return Results.NotFound();
            // Explicit rather than relying solely on the DB's ON DELETE CASCADE, so this behaves
            // identically under providers (e.g. EF InMemory in tests) that don't enforce it themselves.
            var assignments = await db.MailLabelAssignments.Where(x => x.MailAccountId == current.MailAccountId && x.MailLabelId == id).ToListAsync(ct);
            db.MailLabelAssignments.RemoveRange(assignments);
            db.MailLabels.Remove(label);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.LabelDeleted, "MailLabel", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("DeleteLabel").WithSummary("Delete a label").WithDescription("Also strips the label from every mail it was assigned to.").Produces(204).Produces(404);

        api.MapGet("/labels/assignments", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.MailLabelAssignments.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .ToListAsync(ct);
            var grouped = rows.GroupBy(x => x.MailId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(x => x.MailLabelId).ToList());
            return Results.Ok(new LabelAssignmentsResponse(grouped));
        }).WithName("GetLabelAssignments").WithSummary("Full mail-to-label assignment map for the current mailbox").Produces<LabelAssignmentsResponse>();

        api.MapPost("/labels/assignments", async (LabelAssignmentRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            await MutateAssignmentsAsync(db, current.MailAccountId, request, add: true, ct);
            return Results.NoContent();
        }).WithName("AssignLabels").WithSummary("Assign labels to mails").WithDescription("Ids not owned by this account, or already-assigned pairs, are silently skipped.").Produces(204);

        api.MapPost("/labels/assignments/remove", async (LabelAssignmentRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            await MutateAssignmentsAsync(db, current.MailAccountId, request, add: false, ct);
            return Results.NoContent();
        }).WithName("UnassignLabels").WithSummary("Remove labels from mails").Produces(204);
    }

    private static Task<bool> NameTakenAsync(AppDbContext db, Guid accountId, string name, Guid? excludingId, CancellationToken ct) =>
        db.MailLabels.AnyAsync(x => x.MailAccountId == accountId && x.Id != (excludingId ?? Guid.Empty) && x.Name.ToLower() == name.ToLower(), ct);

    private static async Task MutateAssignmentsAsync(AppDbContext db, Guid accountId, LabelAssignmentRequest request, bool add, CancellationToken ct)
    {
        if (request.MailIds.Count == 0 || request.LabelIds.Count == 0) return;
        var ownedLabelIds = await db.MailLabels.Where(x => x.MailAccountId == accountId && request.LabelIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
        var ownedMailIds = await db.Mails.Where(x => x.MailAccountId == accountId && request.MailIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
        if (ownedLabelIds.Count == 0 || ownedMailIds.Count == 0) return;
        if (add)
        {
            var existing = (await db.MailLabelAssignments
                    .Where(x => x.MailAccountId == accountId && ownedMailIds.Contains(x.MailId) && ownedLabelIds.Contains(x.MailLabelId))
                    .Select(x => new { x.MailId, x.MailLabelId })
                    .ToListAsync(ct))
                .Select(x => (x.MailId, x.MailLabelId)).ToHashSet();
            foreach (var mailId in ownedMailIds)
                foreach (var labelId in ownedLabelIds)
                    if (existing.Add((mailId, labelId)))
                        db.MailLabelAssignments.Add(new MailLabelAssignment { Id = Guid.NewGuid(), MailAccountId = accountId, MailId = mailId, MailLabelId = labelId });
        }
        else
        {
            var toRemove = await db.MailLabelAssignments.Where(x => x.MailAccountId == accountId && ownedMailIds.Contains(x.MailId) && ownedLabelIds.Contains(x.MailLabelId)).ToListAsync(ct);
            db.MailLabelAssignments.RemoveRange(toRemove);
        }
        await db.SaveChangesAsync(ct);
    }

    private static LabelResponse ToResponse(MailLabel label) => new(label.Id, label.Name, label.Color, label.SortOrder);
}
