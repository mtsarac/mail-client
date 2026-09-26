using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record SignatureRequest(string? Name, string? BodyText, string? BodyHtml);
public sealed record SignatureResponse(Guid Id, string Name, string BodyText, string? BodyHtml, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record SignatureDefaultsRequest(Guid? NewMailSignatureId, Guid? ReplySignatureId, Guid? ForwardSignatureId);
public sealed record SignatureDefaultsResponse(Guid? NewMailSignatureId, Guid? ReplySignatureId, Guid? ForwardSignatureId);
public sealed record SignatureListResponse(IReadOnlyList<SignatureResponse> Items, SignatureDefaultsResponse Defaults);

public static class SignatureEndpoints
{
    internal const string LegacySignatureName = "İmza";
    internal const int MaxBodyTextLength = 10_000;
    private const int MaxNameLength = 100;
    private const int MaxBodyHtmlLength = 50_000;

    public static void MapSignatureEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/signatures").RequireAuthorization().WithTags("Signatures");

        api.MapGet("", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var account = await db.MailAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == current.MailAccountId, ct);
            if (account is null) return Results.NotFound();
            var items = await db.MailSignatures.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .OrderBy(x => x.Name).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select(x => new SignatureResponse(x.Id, x.Name, x.BodyText, x.BodyHtml, x.CreatedAt, x.UpdatedAt))
                .ToListAsync(ct);
            return Results.Ok(new SignatureListResponse(items, Defaults(account)));
        }).WithName("ListSignatures").WithSummary("List named signatures and the per-mode defaults of the current mailbox")
            .WithDescription("Signatures are inserted by the client; the server never appends one to outgoing mail. `Defaults` names the signature preselected for new mail, replies (reply and reply-all) and forwards; null means no signature for that mode.")
            .Produces<SignatureListResponse>().Produces(404);

        api.MapPost("", async (SignatureRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var (value, error) = Validate(request);
            if (error is not null) return error;
            var now = DateTime.UtcNow;
            var signature = new MailSignature
            {
                Id = Guid.NewGuid(),
                MailAccountId = current.MailAccountId,
                Name = value!.Name,
                BodyText = value.BodyText,
                BodyHtml = value.BodyHtml,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.MailSignatures.Add(signature);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.SignatureCreated, "MailSignature", signature.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/signatures/{signature.Id}", ToResponse(signature));
        }).WithName("CreateSignature").WithSummary("Add a named signature")
            .WithDescription("name: 1-100 characters. bodyText: required, at most 10000 characters. bodyHtml: optional HTML variant, at most 50000 characters. Creating a signature does not change the defaults.")
            .Produces<SignatureResponse>(201).ProducesValidationProblem();

        api.MapPut("/{id:guid}", async (Guid id, SignatureRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var signature = await db.MailSignatures.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (signature is null) return SignatureNotFound();
            var (value, error) = Validate(request);
            if (error is not null) return error;
            signature.Name = value!.Name;
            signature.BodyText = value.BodyText;
            signature.BodyHtml = value.BodyHtml;
            signature.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.SignatureUpdated, "MailSignature", signature.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(ToResponse(signature));
        }).WithName("UpdateSignature").WithSummary("Replace a named signature")
            .Produces<SignatureResponse>().ProducesValidationProblem().ProblemCodes(404, "signature_not_found");

        api.MapDelete("/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var signature = await db.MailSignatures.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (signature is null) return SignatureNotFound();
            var account = await db.MailAccounts.SingleAsync(x => x.Id == current.MailAccountId, ct);
            if (account.DefaultNewSignatureId == id) account.DefaultNewSignatureId = null;
            if (account.DefaultReplySignatureId == id) account.DefaultReplySignatureId = null;
            if (account.DefaultForwardSignatureId == id) account.DefaultForwardSignatureId = null;
            await db.MailIdentities.Where(x => x.MailAccountId == current.MailAccountId && x.SignatureId == id)
                .ForEachAsync(x => x.SignatureId = null, ct);
            db.MailSignatures.Remove(signature);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.SignatureDeleted, "MailSignature", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("DeleteSignature").WithSummary("Delete a named signature")
            .WithDescription("Every default that pointed at the signature is cleared (no signature for that mode).")
            .Produces(204).ProblemCodes(404, "signature_not_found");

        api.MapPut("/defaults", async (SignatureDefaultsRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var requested = new[] { request.NewMailSignatureId, request.ReplySignatureId, request.ForwardSignatureId }
                .OfType<Guid>().Distinct().ToList();
            var owned = await db.MailSignatures.CountAsync(x => x.MailAccountId == current.MailAccountId && requested.Contains(x.Id), ct);
            if (owned != requested.Count) return SignatureNotFound();
            var account = await db.MailAccounts.SingleOrDefaultAsync(x => x.Id == current.MailAccountId, ct);
            if (account is null) return Results.NotFound();
            account.DefaultNewSignatureId = request.NewMailSignatureId;
            account.DefaultReplySignatureId = request.ReplySignatureId;
            account.DefaultForwardSignatureId = request.ForwardSignatureId;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.SignatureDefaultsUpdated, "MailAccount", current.MailAccountId.ToString(), null, correlation.CorrelationId, ct);
            return Results.Ok(Defaults(account));
        }).WithName("UpdateSignatureDefaults").WithSummary("Choose the default signature for new mail, replies and forwards")
            .WithDescription("Replaces all three defaults; null clears that mode. Each id must be a signature of the current mailbox. The new-mail default is also what the legacy `signature` field of GET /api/account reports.")
            .Produces<SignatureDefaultsResponse>().ProblemCodes(404, "signature_not_found");
    }

    internal static async Task<string?> LegacySignatureAsync(AppDbContext db, Guid accountId, Guid? defaultNewSignatureId, CancellationToken ct) =>
        defaultNewSignatureId is { } id
            ? await db.MailSignatures.AsNoTracking().Where(x => x.Id == id && x.MailAccountId == accountId).Select(x => x.BodyText).SingleOrDefaultAsync(ct)
            : null;

    internal static async Task SetLegacySignatureAsync(AppDbContext db, MailAccount account, string? text, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (string.IsNullOrEmpty(text))
        {
            if (account.DefaultNewSignatureId is { } previous)
            {
                account.DefaultNewSignatureId = null;
                if (account.DefaultReplySignatureId == previous) account.DefaultReplySignatureId = null;
                if (account.DefaultForwardSignatureId == previous) account.DefaultForwardSignatureId = null;
            }
            return;
        }
        var existing = account.DefaultNewSignatureId is { } id
            ? await db.MailSignatures.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == account.Id, ct)
            : null;
        if (existing is not null)
        {
            existing.BodyText = text;
            existing.BodyHtml = null;
            existing.UpdatedAt = now;
            return;
        }
        var signature = new MailSignature
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            Name = LegacySignatureName,
            BodyText = text,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MailSignatures.Add(signature);
        account.DefaultNewSignatureId = signature.Id;
        account.DefaultReplySignatureId ??= signature.Id;
        account.DefaultForwardSignatureId ??= signature.Id;
    }

    private sealed record ValidSignature(string Name, string BodyText, string? BodyHtml);

    private static (ValidSignature? Value, IResult? Error) Validate(SignatureRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > MaxNameLength)
            return (null, Invalid("name", "Name must be 1-100 characters."));
        var bodyText = request.BodyText?.Trim() ?? "";
        if (bodyText.Length is < 1 or > MaxBodyTextLength)
            return (null, Invalid("bodyText", "Signature text must be 1-10000 characters."));
        var bodyHtml = string.IsNullOrWhiteSpace(request.BodyHtml) ? null : request.BodyHtml.Trim();
        if (bodyHtml is { Length: > MaxBodyHtmlLength })
            return (null, Invalid("bodyHtml", "Signature HTML must be at most 50000 characters."));
        return (new(name, bodyText, bodyHtml), null);
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult SignatureNotFound() =>
        Results.Problem(statusCode: 404, extensions: new Dictionary<string, object?> { ["code"] = "signature_not_found" });

    private static SignatureDefaultsResponse Defaults(MailAccount account) =>
        new(account.DefaultNewSignatureId, account.DefaultReplySignatureId, account.DefaultForwardSignatureId);

    private static SignatureResponse ToResponse(MailSignature signature) =>
        new(signature.Id, signature.Name, signature.BodyText, signature.BodyHtml, signature.CreatedAt, signature.UpdatedAt);
}
