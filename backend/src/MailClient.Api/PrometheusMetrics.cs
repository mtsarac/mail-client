using OpenTelemetry.Metrics;

namespace MailClient.Api.Observability;

/// <summary>
/// The only code touching the prerelease OpenTelemetry.Exporter.Prometheus.AspNetCore package.
/// It runs only when Observability:Prometheus:Enabled is true; OTLP remains the stable export path.
/// </summary>
internal static class PrometheusMetrics
{
    public static void AddExporter(MeterProviderBuilder metrics) => metrics.AddPrometheusExporter();

    public static void MapScrapingEndpoint(WebApplication app) =>
        app.MapPrometheusScrapingEndpoint(PrometheusOptions.ScrapePath).DisableRateLimiting();
}
