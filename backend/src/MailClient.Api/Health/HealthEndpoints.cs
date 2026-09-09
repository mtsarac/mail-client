using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .WithName("HealthCheck")
            .WithTags("Health");

        app.MapGet("/health/db", async (AppDbContext db, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        {
            try
            {
                await db.Database.OpenConnectionAsync(cancellationToken);
                await db.Database.CloseConnectionAsync();
                return Results.Json(
                    new DbHealthResponse("Healthy", [new DbCheckStatus("postgres", "Healthy", null)]),
                    statusCode: StatusCodes.Status200OK);
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("Health").LogWarning(ex, "Database health check failed.");
                return Results.Json(
                    new DbHealthResponse("Unhealthy", [new DbCheckStatus("postgres", "Unhealthy", "Database connection failed.")]),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithName("DbHealthCheck")
        .WithTags("Health")
        .Produces<DbHealthResponse>(StatusCodes.Status200OK)
        .Produces<DbHealthResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}

sealed record DbCheckStatus(string Name, string Status, string? Error);
sealed record DbHealthResponse(string Status, List<DbCheckStatus> Checks);
