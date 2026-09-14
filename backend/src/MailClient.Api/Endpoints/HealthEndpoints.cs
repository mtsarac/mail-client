namespace MailClient.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).WithName("Health").WithSummary("Check API health").Produces(200);
    }
}
