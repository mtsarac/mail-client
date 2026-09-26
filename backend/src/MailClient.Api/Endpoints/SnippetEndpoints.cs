using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record SnippetRequest(string? Title, string Text, int? SortOrder);
public sealed record SnippetResponse(Guid Id, string? Title, string Text, int SortOrder, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record SnippetListResponse(IReadOnlyList<SnippetResponse> Items);

public static class SnippetEndpoints
{
    private const int MaxTitleLength = 100;
    private const int MaxTextLength = 2000;
    private const int MaxSortOrder = 99999;

    public static void MapSnippetEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Snippets");

        api.MapGet("/snippets", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new SnippetListResponse(await db.MailSnippets.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select(x => new SnippetResponse(x.Id, x.Title, x.Text, x.SortOrder, x.CreatedAt, x.UpdatedAt))
                .ToListAsync(ct))))
            .WithName("ListSnippets").WithSummary("List quick snippets for the current mailbox in display order").Produces<SnippetListResponse>();

        api.MapPost("/snippets", async (SnippetRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var (title, text, error) = Validate(request);
            if (error is not null) return error;
            var sortOrder = request.SortOrder
                ?? 1 + await db.MailSnippets.Where(x => x.MailAccountId == current.MailAccountId).Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? 0;
            var now = DateTime.UtcNow;
            var snippet = new MailSnippet { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, Title = title, Text = text, SortOrder = Math.Min(sortOrder, MaxSortOrder), CreatedAt = now, UpdatedAt = now };
            db.MailSnippets.Add(snippet);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.SnippetCreated, "MailSnippet", snippet.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/snippets/{snippet.Id}", ToResponse(snippet));
        }).WithName("CreateSnippet").WithSummary("Create a quick snippet").WithDescription("Without sortOrder the snippet is appended after the existing ones.")
            .Produces<SnippetResponse>(201).ProducesValidationProblem();

        api.MapPut("/snippets/{id:guid}", async (Guid id, SnippetRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var snippet = await db.MailSnippets.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (snippet is null) return Results.NotFound();
            var (title, text, error) = Validate(request);
            if (error is not null) return error;
            snippet.Title = title;
            snippet.Text = text;
            snippet.SortOrder = request.SortOrder ?? snippet.SortOrder;
            snippet.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.SnippetUpdated, "MailSnippet", snippet.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(ToResponse(snippet));
        }).WithName("UpdateSnippet").WithSummary("Replace a quick snippet").WithDescription("Without sortOrder the current position is kept.")
            .Produces<SnippetResponse>().Produces(404).ProducesValidationProblem();

        api.MapDelete("/snippets/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var snippet = await db.MailSnippets.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (snippet is null) return Results.NotFound();
            db.MailSnippets.Remove(snippet);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.SnippetDeleted, "MailSnippet", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("DeleteSnippet").WithSummary("Delete a quick snippet").Produces(204).Produces(404);
    }

    private static (string? Title, string Text, IResult? Error) Validate(SnippetRequest request)
    {
        var title = request.Title?.Trim();
        if (title is { Length: 0 }) title = null;
        var text = (request.Text ?? "").Trim();
        var errors = new Dictionary<string, string[]>();
        if (title is { Length: > MaxTitleLength })
            errors["title"] = [$"Title is at most {MaxTitleLength} characters."];
        if (text.Length is 0 or > MaxTextLength)
            errors["text"] = [$"Snippet text must be 1-{MaxTextLength} characters."];
        if (request.SortOrder is < 0 or > MaxSortOrder)
            errors["sortOrder"] = [$"Sort order must be 0-{MaxSortOrder}."];
        return (title, text, errors.Count == 0 ? null : Results.ValidationProblem(errors));
    }

    private static SnippetResponse ToResponse(MailSnippet snippet) =>
        new(snippet.Id, snippet.Title, snippet.Text, snippet.SortOrder, snippet.CreatedAt, snippet.UpdatedAt);
}
