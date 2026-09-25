using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public static class RuleEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ConditionTypes = ["senderContains", "senderEquals", "senderDomain", "subjectContains", "recipientContains", "hasAttachment", "folder"];
    private static readonly HashSet<string> ActionTypes = ["markRead", "markUnread", "star", "archive", "move", "trash", "spam", "addLabel", "stopProcessing"];

    public static void MapRuleEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/rules").RequireAuthorization().WithTags("Rules");
        api.MapGet("", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var rules = await db.MailRules.AsNoTracking().Where(x => x.MailAccountId == current.MailAccountId)
                .OrderBy(x => x.Priority).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);
            return Results.Ok(rules.Select(ToResponse).ToList());
        }).WithName("ListRules").WithSummary("List mail rules for the current mailbox in evaluation order").Produces<List<RuleResponse>>();

        api.MapPost("", async (RuleRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            if (request.LegacyId is { } legacyId)
            {
                if (legacyId.Length is < 1 or > 150) return Invalid("legacyId");
                var existing = await db.MailRules.AsNoTracking().SingleOrDefaultAsync(
                    x => x.MailAccountId == current.MailAccountId && x.LegacyId == legacyId, ct);
                if (existing is not null) return Results.Ok(ToResponse(existing));
            }
            var error = await ValidateAsync(request, current.MailAccountId, db, ct);
            if (error is not null) return Invalid(error);
            var rule = new MailRule
            {
                Id = Guid.NewGuid(),
                MailAccountId = current.MailAccountId,
                LegacyId = request.LegacyId,
                CreatedAt = DateTime.UtcNow
            };
            Apply(rule, request);
            db.MailRules.Add(rule);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (request.LegacyId is not null)
            {
                db.ChangeTracker.Clear();
                var migrated = await db.MailRules.AsNoTracking().SingleAsync(
                    x => x.MailAccountId == current.MailAccountId && x.LegacyId == request.LegacyId, ct);
                return Results.Ok(ToResponse(migrated));
            }
            return Results.Created($"/api/rules/{rule.Id}", ToResponse(rule));
        }).WithName("CreateRule").WithSummary("Create a mail rule")
            .WithDescription("A request with legacyId is idempotent per account: repeating it returns the already migrated rule with 200.")
            .Produces<RuleResponse>(201).Produces<RuleResponse>().ProducesValidationProblem();

        api.MapPut("/{id:guid}", async (Guid id, RuleRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var rule = await db.MailRules.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (rule is null) return Results.NotFound();
            var error = await ValidateAsync(request, current.MailAccountId, db, ct);
            if (error is not null) return Invalid(error);
            Apply(rule, request);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(rule));
        }).WithName("UpdateRule").WithSummary("Replace a mail rule").Produces<RuleResponse>().Produces(404).ProducesValidationProblem();

        api.MapDelete("/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var rule = await db.MailRules.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (rule is null) return Results.NotFound();
            db.MailRules.Remove(rule);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteRule").WithSummary("Delete a mail rule").Produces(204).Produces(404);
    }

    private static IResult Invalid(string field) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = ["Invalid rule definition."] });

    private static async Task<string?> ValidateAsync(RuleRequest request, Guid accountId, AppDbContext db, CancellationToken ct)
    {
        if (request.Name is null || request.Name.Trim().Length is < 1 or > 100) return "name";
        if (request.Priority is < 0 or > 99999) return "priority";
        if (request.Logic is not ("And" or "Or")) return "logic";
        if (request.Conditions is null || request.Conditions.Count is < 1 or > 16) return "conditions";
        if (request.Actions is null || request.Actions.Count is < 1 or > 16) return "actions";
        foreach (var condition in request.Conditions)
        {
            if (condition is null || !ConditionTypes.Contains(condition.Type)) return "conditions";
            if (condition.Type == "hasAttachment")
            {
                if (condition.Value is not null) return "conditions";
                continue;
            }
            if (condition.Value is null || condition.Value.Trim().Length is < 1 or > 320) return "conditions";
            if (condition.Type == "folder" && (!Guid.TryParse(condition.Value, out var folderId)
                || !await db.MailFolders.AnyAsync(x => x.Id == folderId && x.MailAccountId == accountId && x.IsAvailable, ct)))
                return "conditions";
        }
        foreach (var action in request.Actions)
        {
            if (action is null || !ActionTypes.Contains(action.Type)) return "actions";
            if (action.Type == "move")
            {
                if (action.FolderId is not { } folderId || action.LabelId is not null
                    || !await db.MailFolders.AnyAsync(x => x.Id == folderId && x.MailAccountId == accountId && x.IsAvailable, ct))
                    return "actions";
            }
            else if (action.Type == "addLabel")
            {
                if (action.LabelId is not { } labelId || action.FolderId is not null
                    || !await db.MailLabels.AnyAsync(x => x.Id == labelId && x.MailAccountId == accountId, ct))
                    return "actions";
            }
            else if (action.FolderId is not null || action.LabelId is not null) return "actions";
        }
        return null;
    }

    private static void Apply(MailRule rule, RuleRequest request)
    {
        rule.Name = request.Name.Trim();
        rule.Enabled = request.Enabled;
        rule.Priority = request.Priority;
        rule.Logic = request.Logic;
        rule.ConditionJson = JsonSerializer.Serialize(request.Conditions, JsonOptions);
        rule.ActionJson = JsonSerializer.Serialize(request.Actions, JsonOptions);
        rule.UpdatedAt = DateTime.UtcNow;
    }

    private static RuleResponse ToResponse(MailRule rule) => new(rule.Id, rule.Name, rule.Enabled, rule.Priority,
        rule.Logic, JsonSerializer.Deserialize<List<RuleCondition>>(rule.ConditionJson, JsonOptions)!,
        JsonSerializer.Deserialize<List<RuleAction>>(rule.ActionJson, JsonOptions)!, rule.CreatedAt, rule.UpdatedAt);
}
