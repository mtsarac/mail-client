using MailClient.Api.Auth;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record DeviceRegistrationRequest(string Token, string Platform, string? AppVersion, string? Locale);

public sealed record DeviceResponse(Guid Id, string Platform, string? AppVersion, string? Locale, DateTime RegisteredAt, DateTime? LastSeenAt);

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Devices");
        api.MapPost("/devices", async (DeviceRegistrationRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);
            var now = DateTime.UtcNow;
            var existing = await db.DeviceTokens.SingleOrDefaultAsync(x => x.MailAccountId == current.MailAccountId && x.Token == request.Token, ct);
            if (existing is null)
            {
                existing = new DeviceToken
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = current.MailAccountId,
                    Token = request.Token,
                    Platform = request.Platform,
                    AppVersion = request.AppVersion,
                    Locale = request.Locale,
                    RegisteredAt = now
                };
                db.DeviceTokens.Add(existing);
            }
            else
            {
                existing.Platform = request.Platform;
                existing.AppVersion = request.AppVersion;
                existing.Locale = request.Locale;
                existing.LastSeenAt = now;
            }

            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.DeviceRegistered, "DeviceToken", existing.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/devices/{existing.Id}", ToResponse(existing));
        }).WithName("RegisterDevice").WithSummary("Register device for current mailbox").WithDescription("Idempotent per (mailbox, token): re-registering the same push token updates it instead of duplicating. Always answers 201.").Produces<DeviceResponse>(201).ProducesValidationProblem();
        api.MapDelete("/devices/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            var token = await db.DeviceTokens.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
            if (token is null) return Results.NotFound();
            db.Remove(token);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.DeviceRemoved, "DeviceToken", id.ToString(), null, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithName("DeleteDevice").WithSummary("Remove account-owned device").Produces(204).Produces(404);
    }

    private static Dictionary<string, string[]> Validate(DeviceRegistrationRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 500)
            errors["token"] = ["Device token is required and at most 500 characters."];
        if (request.Platform?.Length > 100)
            errors["platform"] = ["Platform is at most 100 characters."];
        if (request.AppVersion?.Length > 50)
            errors["appVersion"] = ["App version is at most 50 characters."];
        if (request.Locale?.Length > 35)
            errors["locale"] = ["Locale is at most 35 characters."];
        return errors;
    }

    private static DeviceResponse ToResponse(DeviceToken token) =>
        new(token.Id, token.Platform, token.AppVersion, token.Locale, token.RegisteredAt, token.LastSeenAt);
}
