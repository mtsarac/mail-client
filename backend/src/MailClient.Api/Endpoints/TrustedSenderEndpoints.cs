using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record TrustedSenderRequest(TrustedSenderKind Kind, string Value);
public sealed record TrustedSenderResponse(Guid Id, TrustedSenderKind Kind, string Value, DateTime CreatedAt);
public sealed record TrustedSenderListResponse(IReadOnlyList<TrustedSenderResponse> Items);

public static class TrustedSenderEndpoints
{
    private const int MaxValueLength = 320;

    public static void MapTrustedSenderEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Trusted Senders");

        api.MapGet("/trusted-senders", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new TrustedSenderListResponse(await db.TrustedSenders.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .OrderBy(x => x.Kind).ThenBy(x => x.Value)
                .Select(x => new TrustedSenderResponse(x.Id, x.Kind, x.Value, x.CreatedAt))
                .ToListAsync(ct))))
            .WithName("ListTrustedSenders").WithSummary("List senders and domains whose remote images load automatically")
            .Produces<TrustedSenderListResponse>();

        api.MapPost("/trusted-senders", async (TrustedSenderRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var value = Normalize(request.Kind, request.Value);
            if (value is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["value"] = [request.Kind == TrustedSenderKind.Domain ? "A valid domain is required." : "A valid email address is required."] });
            var existing = await db.TrustedSenders.AsNoTracking()
                .SingleOrDefaultAsync(x => x.MailAccountId == current.MailAccountId && x.Kind == request.Kind && x.Value == value, ct);
            if (existing is not null)
                return Results.Ok(ToResponse(existing));
            var entity = new TrustedSender { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, Kind = request.Kind, Value = value, CreatedAt = DateTime.UtcNow };
            db.TrustedSenders.Add(entity);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var raced = await db.TrustedSenders.AsNoTracking()
                    .SingleAsync(x => x.MailAccountId == current.MailAccountId && x.Kind == request.Kind && x.Value == value, ct);
                return Results.Ok(ToResponse(raced));
            }
            await audit.WriteAsync(current.MailAccountId, AuditActions.TrustedSenderAdded, "TrustedSender", entity.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/trusted-senders/{entity.Id}", ToResponse(entity));
        }).WithName("AddTrustedSender").WithSummary("Always load remote images from a sender or domain")
            .WithDescription("Idempotent: an existing entry returns 200 with the stored row. Values are normalized to lower case; Junk mail and mail failing DMARC never load remote images automatically.")
            .Produces<TrustedSenderResponse>(201).Produces<TrustedSenderResponse>().ProducesValidationProblem();

        api.MapDelete("/trusted-senders/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var entity = await db.TrustedSenders.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (entity is null) return Results.NotFound();
            db.TrustedSenders.Remove(entity);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.TrustedSenderRemoved, "TrustedSender", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("RemoveTrustedSender").WithSummary("Stop loading remote images automatically for a sender or domain")
            .Produces(204).Produces(404);
    }

    private static TrustedSenderResponse ToResponse(TrustedSender entity) =>
        new(entity.Id, entity.Kind, entity.Value, entity.CreatedAt);

    public static string? Normalize(TrustedSenderKind kind, string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength || value.Any(char.IsWhiteSpace))
            return null;
        if (kind == TrustedSenderKind.Sender)
        {
            var at = value.IndexOf('@');
            return at > 0 && at == value.LastIndexOf('@') && ValidDomain(value[(at + 1)..]) ? value : null;
        }
        if (value.StartsWith('@')) value = value[1..];
        return ValidDomain(value) ? value : null;
    }

    private static bool ValidDomain(string domain) =>
        domain.Length is > 2 and <= 253
        && domain.Contains('.')
        && !domain.StartsWith('.') && !domain.EndsWith('.')
        && !domain.Contains("..")
        && Uri.CheckHostName(domain) == UriHostNameType.Dns;
}
