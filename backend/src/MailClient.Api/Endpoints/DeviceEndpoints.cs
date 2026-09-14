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

public sealed record DeviceRequest(string Token, string Platform);

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/devices", async (DeviceRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, CorrelationContext correlation, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = ["Device token is required and at most 500 characters."] });
            var now = DateTime.UtcNow;
            var existing = await db.DeviceTokens.SingleOrDefaultAsync(x => x.MailAccountId == current.MailAccountId && x.Token == request.Token, ct);
            if (existing is null)
            {
                existing = new DeviceToken { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, Token = request.Token, Platform = request.Platform, RegisteredAt = now };
                db.DeviceTokens.Add(existing);
            }
            else
            {
                existing.Platform = request.Platform;
                existing.LastSeenAt = now;
            }

            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(current.MailAccountId, AuditActions.DeviceRegistered, "DeviceToken", existing.Id.ToString(), null, correlation.CorrelationId, ct);
            return Results.Created($"/api/devices/{existing.Id}", existing);
        }).WithName("RegisterDevice").WithSummary("Register device for current mailbox").Produces<DeviceToken>(201).ProducesValidationProblem();
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
}
