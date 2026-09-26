using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Application.Runtime;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record TemplateRequest(string Name, string? Subject, string? BodyText, string? BodyHtml);
public sealed record TemplateResponse(Guid Id, string Name, string Subject, string? BodyText, string? BodyHtml, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record TemplateListResponse(IReadOnlyList<TemplateResponse> Items);

public static class TemplateEndpoints
{
    private const int MaxNameLength = 100;

    public static void MapTemplateEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Templates");

        api.MapGet("/templates", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new TemplateListResponse(await db.MailTemplates.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .OrderBy(x => x.NormalizedName)
                .Select(x => new TemplateResponse(x.Id, x.Name, x.Subject, x.BodyText, x.BodyHtml, x.CreatedAt, x.UpdatedAt))
                .ToListAsync(ct))))
            .WithName("ListTemplates").WithSummary("List compose templates for the current mailbox").Produces<TemplateListResponse>();

        api.MapPost("/templates", async (TemplateRequest request, ICurrentMailAccount current, AppDbContext db, IRuntimeSettingsStore settings, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var (fields, error) = Validate(request, (await settings.GetAsync(ct)).Settings.Limits.MaxSendBodyChars);
            if (error is not null) return error;
            if (await NameTakenAsync(db, current.MailAccountId, fields.NormalizedName, null, ct))
                return NameTaken();
            var now = DateTime.UtcNow;
            var template = new MailTemplate { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, CreatedAt = now, UpdatedAt = now };
            Apply(template, fields);
            db.MailTemplates.Add(template);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.TemplateCreated, "MailTemplate", template.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/templates/{template.Id}", ToResponse(template));
        }).WithName("CreateTemplate").WithSummary("Create a compose template").Produces<TemplateResponse>(201).ProducesValidationProblem().ProblemCodes(409, "template_name_taken");

        api.MapPut("/templates/{id:guid}", async (Guid id, TemplateRequest request, ICurrentMailAccount current, AppDbContext db, IRuntimeSettingsStore settings, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var template = await db.MailTemplates.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (template is null) return Results.NotFound();
            var (fields, error) = Validate(request, (await settings.GetAsync(ct)).Settings.Limits.MaxSendBodyChars);
            if (error is not null) return error;
            if (await NameTakenAsync(db, current.MailAccountId, fields.NormalizedName, id, ct))
                return NameTaken();
            Apply(template, fields);
            template.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.TemplateUpdated, "MailTemplate", template.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(ToResponse(template));
        }).WithName("UpdateTemplate").WithSummary("Replace a compose template").Produces<TemplateResponse>().Produces(404).ProducesValidationProblem().ProblemCodes(409, "template_name_taken");

        api.MapDelete("/templates/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var template = await db.MailTemplates.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (template is null) return Results.NotFound();
            db.MailTemplates.Remove(template);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.TemplateDeleted, "MailTemplate", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("DeleteTemplate").WithSummary("Delete a compose template").Produces(204).Produces(404);
    }

    private sealed record TemplateFields(string Name, string NormalizedName, string Subject, string? BodyText, string? BodyHtml);

    private static (TemplateFields Fields, IResult? Error) Validate(TemplateRequest request, int maxBodyChars)
    {
        var name = (request.Name ?? "").Trim();
        var subject = (request.Subject ?? "").Trim();
        var bodyText = string.IsNullOrWhiteSpace(request.BodyText) ? null : request.BodyText;
        var bodyHtml = string.IsNullOrWhiteSpace(request.BodyHtml) ? null : request.BodyHtml;
        var fields = new TemplateFields(name, name.ToUpperInvariant(), subject, bodyText, bodyHtml);
        var errors = new Dictionary<string, string[]>();
        if (name.Length is 0 or > MaxNameLength)
            errors["name"] = [$"Template name must be 1-{MaxNameLength} characters."];
        if (subject.Length > MailFieldLimits.Subject || subject.Contains('\r') || subject.Contains('\n'))
            errors["subject"] = [$"Subject must be a single line of at most {MailFieldLimits.Subject} characters."];
        if (bodyText is null && bodyHtml is null)
            errors["body"] = ["Provide bodyText and/or bodyHtml."];
        else if ((bodyText?.Length ?? 0) > maxBodyChars || (bodyHtml?.Length ?? 0) > maxBodyChars)
            errors["body"] = [$"Body is at most {maxBodyChars} characters."];
        return (fields, errors.Count == 0 ? null : Results.ValidationProblem(errors));
    }

    private static void Apply(MailTemplate template, TemplateFields fields)
    {
        template.Name = fields.Name;
        template.NormalizedName = fields.NormalizedName;
        template.Subject = fields.Subject;
        template.BodyText = fields.BodyText;
        template.BodyHtml = fields.BodyHtml;
    }

    private static Task<bool> NameTakenAsync(AppDbContext db, Guid accountId, string normalizedName, Guid? excludingId, CancellationToken ct) =>
        db.MailTemplates.AnyAsync(x => x.MailAccountId == accountId && x.Id != (excludingId ?? Guid.Empty) && x.NormalizedName == normalizedName, ct);

    private static IResult NameTaken() =>
        Results.Problem(statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "template_name_taken" });

    private static TemplateResponse ToResponse(MailTemplate template) =>
        new(template.Id, template.Name, template.Subject, template.BodyText, template.BodyHtml, template.CreatedAt, template.UpdatedAt);
}
