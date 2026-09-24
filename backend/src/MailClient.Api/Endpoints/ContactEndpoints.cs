using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record ContactRequest(string Email, string? DisplayName);
public sealed record ContactResponse(Guid Id, string Email, string? DisplayName, DateTime CreatedAtUtc);
public sealed record ContactListResponse(IReadOnlyList<ContactResponse> Items);

/// <summary>
/// Manually-added contacts only — the app derives most suggestions from mail participants locally
/// (already effectively consistent across devices since every device syncs the same mail). This exists
/// for people the user wants suggested before ever exchanging mail with them.
/// </summary>
public static class ContactEndpoints
{
    public static void MapContactEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Contacts");

        api.MapGet("/contacts", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new ContactListResponse(await db.Contacts.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .OrderBy(x => x.DisplayName ?? x.Email)
                .Select(x => new ContactResponse(x.Id, x.Email, x.DisplayName, x.CreatedAtUtc))
                .ToListAsync(ct))))
            .WithName("ListContacts").WithSummary("List manually-added contacts for the current mailbox").Produces<ContactListResponse>();

        api.MapPost("/contacts", async (ContactRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var (email, displayName, error) = Validate(request);
            if (error is not null) return error;
            var normalized = email.ToUpperInvariant();
            if (await db.Contacts.AnyAsync(x => x.MailAccountId == current.MailAccountId && x.NormalizedEmail == normalized, ct))
                return Results.Problem(statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "contact_already_exists" });
            var contact = new Contact { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, Email = email, NormalizedEmail = normalized, DisplayName = displayName, CreatedAtUtc = DateTime.UtcNow };
            db.Contacts.Add(contact);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.ContactCreated, "Contact", contact.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/contacts/{contact.Id}", ToResponse(contact));
        }).WithName("CreateContact").WithSummary("Add a contact").Produces<ContactResponse>(201).ProducesValidationProblem().ProblemCodes(409, "contact_already_exists");

        api.MapPut("/contacts/{id:guid}", async (Guid id, ContactRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var contact = await db.Contacts.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (contact is null) return Results.NotFound();
            var (email, displayName, error) = Validate(request);
            if (error is not null) return error;
            var normalized = email.ToUpperInvariant();
            if (await db.Contacts.AnyAsync(x => x.MailAccountId == current.MailAccountId && x.Id != id && x.NormalizedEmail == normalized, ct))
                return Results.Problem(statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "contact_already_exists" });
            contact.Email = email;
            contact.NormalizedEmail = normalized;
            contact.DisplayName = displayName;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.ContactUpdated, "Contact", contact.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(ToResponse(contact));
        }).WithName("UpdateContact").WithSummary("Edit a contact").Produces<ContactResponse>().Produces(404).ProducesValidationProblem().ProblemCodes(409, "contact_already_exists");

        api.MapDelete("/contacts/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var contact = await db.Contacts.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (contact is null) return Results.NotFound();
            db.Contacts.Remove(contact);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.ContactDeleted, "Contact", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("DeleteContact").WithSummary("Remove a contact").Produces(204).Produces(404);
    }

    private static (string Email, string? DisplayName, IResult? Error) Validate(ContactRequest request)
    {
        var email = request.Email.Trim();
        if (email.Length == 0 || email.Length > 320 || !email.Contains('@'))
            return (email, null, Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["A valid email address is required."] }));
        var displayName = request.DisplayName?.Trim();
        if (displayName is { Length: 0 }) displayName = null;
        if (displayName is { Length: > 250 })
            return (email, displayName, Results.ValidationProblem(new Dictionary<string, string[]> { ["displayName"] = ["Display name is at most 250 characters."] }));
        return (email, displayName, null);
    }

    private static ContactResponse ToResponse(Contact contact) => new(contact.Id, contact.Email, contact.DisplayName, contact.CreatedAtUtc);
}
