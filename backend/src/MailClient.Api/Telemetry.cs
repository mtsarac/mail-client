using System.Diagnostics;
using System.Reflection;
using MailClient.Application.Observability;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MailClient.Api.Observability;

public static class Telemetry
{
    internal static readonly string[] BuiltInMeters =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Diagnostics",
        "System.Runtime"
    ];

    public static void AddMailClientTelemetry(this WebApplicationBuilder builder, ObservabilityOptions options)
    {
        builder.Services.AddSingleton<MailClientMetrics>();
        if (!options.Tracing.Enabled && !options.Metrics.Enabled)
            return;

        var version = typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var environment = builder.Environment.EnvironmentName;
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(options.ServiceName, serviceVersion: version, autoGenerateServiceInstanceId: true)
                .AddAttributes(
                [
                    new("deployment.environment", environment),
                    new("deployment.environment.name", environment)
                ]));

        if (options.Tracing.Enabled)
        {
            otel.WithTracing(tracing =>
            {
                tracing.AddSource(MailClientTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.RecordException = false;
                        instrumentation.Filter = context => !IsOperationalPath(context.Request.Path);
                        instrumentation.EnrichWithHttpResponse = (activity, _) => RemoveIdentifyingHttpTags(activity);
                    });
                if (options.Otlp.Enabled)
                    tracing.AddOtlpExporter(exporter => ConfigureOtlp(exporter, options.Otlp, "/v1/traces"));
            });
        }

        if (options.Metrics.Enabled)
        {
            otel.WithMetrics(metrics =>
            {
                metrics.AddMeter(MailClientTelemetry.MeterName).AddMeter(BuiltInMeters);
                if (options.Otlp.Enabled)
                    metrics.AddOtlpExporter(exporter => ConfigureOtlp(exporter, options.Otlp, "/v1/metrics"));
                if (options.Prometheus.Enabled)
                    PrometheusMetrics.AddExporter(metrics);
            });
        }
    }

    public static void MapMailClientTelemetry(this WebApplication app, ObservabilityOptions options)
    {
        if (options.Metrics.Enabled && options.Prometheus.Enabled)
            PrometheusMetrics.MapScrapingEndpoint(app);
    }

    internal static bool IsOperationalPath(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments(PrometheusOptions.ScrapePath);

    // Paths carry mail/folder/conversation GUIDs and queries carry search text; http.route keeps the bounded template.
    internal static void RemoveIdentifyingHttpTags(Activity activity)
    {
        activity.SetTag("url.path", null);
        activity.SetTag("url.query", null);
        activity.SetTag("url.full", null);
    }

    private static void ConfigureOtlp(OtlpExporterOptions exporter, OtlpOptions otlp, string signalPath)
    {
        if (otlp.Protocol is { } protocol)
            exporter.Protocol = protocol;
        if (string.IsNullOrWhiteSpace(otlp.Endpoint))
            return;
        var endpoint = otlp.Endpoint.TrimEnd('/');
        exporter.Endpoint = new Uri(exporter.Protocol == OtlpExportProtocol.HttpProtobuf ? endpoint + signalPath : endpoint);
    }
}
