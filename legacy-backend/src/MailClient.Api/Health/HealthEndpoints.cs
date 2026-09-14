using MailClient.Api.Docs;
using MailClient.Application.Interfaces;

// Maps anonymous liveness (/health) and Postgres readiness (/health/db) endpoints.
namespace MailClient.Api.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
            .WithName("HealthCheck")
            .WithTags("Health")
            .WithSummary("Liveness check")
            .WithDescription("Anonymous. Returns ok when the process is running.")
            .Produces<HealthResponse>(StatusCodes.Status200OK);

        app.MapGet("/health/db", async (IHealthProbe probe, CancellationToken cancellationToken) =>
        {
            if (await probe.CheckDatabaseAsync(cancellationToken))
            {
                return Results.Json(
                    new DbHealthResponse("Healthy", [new DbCheckStatus("postgres", "Healthy", null)]),
                    statusCode: StatusCodes.Status200OK);
            }

            return Results.Json(
                new DbHealthResponse("Unhealthy", [new DbCheckStatus("postgres", "Unhealthy", "Database connection failed.")]),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("DbHealthCheck")
        .WithTags("Health")
        .WithSummary("Postgres readiness check")
        .WithDescription("Anonymous. Returns 200 when Postgres answers, 503 otherwise.")
        .Produces<DbHealthResponse>(StatusCodes.Status200OK)
        .Produces<DbHealthResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}

sealed record DbCheckStatus(string Name, string Status, string? Error);
sealed record DbHealthResponse(string Status, List<DbCheckStatus> Checks);
