using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MailClient.Api.Endpoints;

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddMailClientHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<StorageHealthCheck>("storage", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(5));
        return services;
    }

    public static void MapHealthEndpoints(this WebApplication app)
    {
        var live = new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteResponseAsync };
        app.MapHealthChecks("/health/live", live).WithName("HealthLive").DisableRateLimiting();
        app.MapHealthChecks("/health", live).WithName("Health").DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteResponseAsync
        }).WithName("HealthReady").DisableRateLimiting();
    }

    private static Task WriteResponseAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString())
        });
}

internal sealed class PostgresHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database is unreachable.");
}

internal sealed class StorageHealthCheck(LocalAttachmentStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(storage.RootPath);
            var probe = Path.Combine(storage.RootPath, $".health-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(probe, [], cancellationToken);
            File.Delete(probe);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy("Attachment storage is not writable.");
        }
    }
}
