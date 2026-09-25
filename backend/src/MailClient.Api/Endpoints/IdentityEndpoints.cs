using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailClient.Api.Endpoints;

public sealed record IdentityRequest(string? EmailAddress, string? DisplayName, string? ReplyTo, Guid? SignatureId, bool IsDefault);
public sealed record IdentityResponse(Guid Id, string EmailAddress, string DisplayName, string? ReplyTo, Guid? SignatureId, bool IsDefault);
public sealed record IdentityListResponse(IReadOnlyList<IdentityResponse> Items);

public static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/identities").RequireAuthorization().WithTags("Identities");
        api.MapGet("", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => Results.Ok(new IdentityListResponse(
            await db.MailIdentities.AsNoTracking().Where(x => x.MailAccountId == current.MailAccountId)
                .OrderByDescending(x => x.IsDefault).ThenBy(x => x.DisplayName).ThenBy(x => x.EmailAddress)
                .Select(x => new IdentityResponse(x.Id, x.EmailAddress, x.DisplayName, x.ReplyTo, x.SignatureId, x.IsDefault)).ToListAsync(ct))))
            .WithName("ListIdentities").WithSummary("List optional sender identities for the current mailbox").Produces<IdentityListResponse>();

        api.MapPost("", async (IdentityRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var (value, error) = await ValidateAsync(request, null, current.MailAccountId, db, ct);
            if (error is not null) return error;
            var identity = new MailIdentity { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId };
            Apply(identity, value!);
            if (identity.IsDefault) await ClearDefaultAsync(db, current.MailAccountId, null, ct);
            db.MailIdentities.Add(identity);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.IdentityCreated, "MailIdentity", identity.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/identities/{identity.Id}", ToResponse(identity));
        }).WithName("CreateIdentity").WithSummary("Add a sender identity").Produces<IdentityResponse>(201)
            .ProducesValidationProblem().ProblemCodes(404, "signature_not_found").ProblemCodes(409, "identity_already_exists");

        api.MapPut("/{id:guid}", async (Guid id, IdentityRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var identity = await db.MailIdentities.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (identity is null) return IdentityNotFound();
            var (value, error) = await ValidateAsync(request, id, current.MailAccountId, db, ct);
            if (error is not null) return error;
            if (value!.IsDefault) await ClearDefaultAsync(db, current.MailAccountId, id, ct);
            Apply(identity, value);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.IdentityUpdated, "MailIdentity", identity.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(ToResponse(identity));
        }).WithName("UpdateIdentity").WithSummary("Replace a sender identity").Produces<IdentityResponse>()
            .ProducesValidationProblem().ProblemCodes(404, "identity_not_found", "signature_not_found").ProblemCodes(409, "identity_already_exists");

        api.MapDelete("/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var identity = await db.MailIdentities.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (identity is null) return IdentityNotFound();
            if (await db.ScheduledSends.AnyAsync(x => x.IdentityId == id && x.Status == MailClient.Domain.Enums.ScheduledSendStatus.Pending, ct))
                return Results.Problem(statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "identity_in_use" });
            db.MailIdentities.Remove(identity);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.IdentityDeleted, "MailIdentity", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("DeleteIdentity").WithSummary("Delete a sender identity").Produces(204)
            .ProblemCodes(404, "identity_not_found").ProblemCodes(409, "identity_in_use");
    }

    private sealed record ValidIdentity(string EmailAddress, string DisplayName, string? ReplyTo, Guid? SignatureId, bool IsDefault);

    private static async Task<(ValidIdentity? Value, IResult? Error)> ValidateAsync(IdentityRequest request, Guid? id, Guid accountId, AppDbContext db, CancellationToken ct)
    {
        var email = Address(request.EmailAddress);
        if (email is null) return (null, Invalid("emailAddress"));
        var displayName = request.DisplayName?.Trim() ?? "";
        if (displayName.Length > 250 || displayName.Any(char.IsControl)) return (null, Invalid("displayName"));
        var replyTo = string.IsNullOrWhiteSpace(request.ReplyTo) ? null : Address(request.ReplyTo);
        if (request.ReplyTo is not null && replyTo is null) return (null, Invalid("replyTo"));
        if (request.SignatureId is { } signatureId && !await db.MailSignatures.AnyAsync(x => x.Id == signatureId && x.MailAccountId == accountId, ct))
            return (null, Results.Problem(statusCode: 404, extensions: new Dictionary<string, object?> { ["code"] = "signature_not_found" }));
        if (await db.MailIdentities.AnyAsync(x => x.MailAccountId == accountId && x.Id != id && x.EmailAddress.ToUpper() == email.ToUpper(), ct))
            return (null, Results.Problem(statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "identity_already_exists" }));
        return (new(email, displayName, replyTo, request.SignatureId, request.IsDefault), null);
    }

    private static string? Address(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320 || value.Any(char.IsControl) || !MailboxAddress.TryParse(value.Trim(), out var parsed)) return null;
        return (parsed?.Name?.Length ?? 1) == 0 && (parsed?.Address?.Contains('@', StringComparison.Ordinal) ?? false) ? parsed!.Address : null;
    }

    private static Task ClearDefaultAsync(AppDbContext db, Guid accountId, Guid? except, CancellationToken ct) =>
        db.MailIdentities.Where(x => x.MailAccountId == accountId && x.Id != except && x.IsDefault).ForEachAsync(x => x.IsDefault = false, ct);

    private static void Apply(MailIdentity identity, ValidIdentity value)
    {
        identity.EmailAddress = value.EmailAddress;
        identity.DisplayName = value.DisplayName;
        identity.ReplyTo = value.ReplyTo;
        identity.SignatureId = value.SignatureId;
        identity.IsDefault = value.IsDefault;
    }

    private static IResult Invalid(string field) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = ["Invalid identity value."] });
    private static IResult IdentityNotFound() => Results.Problem(statusCode: 404, extensions: new Dictionary<string, object?> { ["code"] = "identity_not_found" });
    private static IdentityResponse ToResponse(MailIdentity x) => new(x.Id, x.EmailAddress, x.DisplayName, x.ReplyTo, x.SignatureId, x.IsDefault);
}
