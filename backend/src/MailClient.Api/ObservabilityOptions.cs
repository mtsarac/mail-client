using OpenTelemetry.Exporter;

namespace MailClient.Api.Observability;

/// <summary>
/// Static deployment configuration for telemetry export and log files. Never stored in database-backed runtime settings.
/// </summary>
public sealed class ObservabilityOptions
{
    public string ServiceName { get; set; } = "mail-client";
    public TelemetrySignalOptions Tracing { get; set; } = new();
    public TelemetrySignalOptions Metrics { get; set; } = new();
    public OtlpOptions Otlp { get; set; } = new();
    public PrometheusOptions Prometheus { get; set; } = new();
    public LogFilesOptions Logs { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
            throw new InvalidOperationException("Observability:ServiceName is required.");
        if (Prometheus.Enabled && !Metrics.Enabled)
            throw new InvalidOperationException("Observability:Prometheus:Enabled requires Observability:Metrics:Enabled.");
        if (Otlp.Enabled && !string.IsNullOrWhiteSpace(Otlp.Endpoint) && !Uri.TryCreate(Otlp.Endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("Observability:Otlp:Endpoint must be an absolute URI.");
        Logs.App.Validate("App");
        Logs.Http.Validate("Http");
    }
}

public sealed class TelemetrySignalOptions
{
    public bool Enabled { get; set; } = true;
}

public sealed class OtlpOptions
{
    public bool Enabled { get; set; }

    /// <summary>Collector base URI. When empty, the standard OTEL_EXPORTER_OTLP_ENDPOINT variable (or the exporter default) applies.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Grpc or HttpProtobuf. When empty, OTEL_EXPORTER_OTLP_PROTOCOL (or the exporter default) applies.</summary>
    public OtlpExportProtocol? Protocol { get; set; }
}

public sealed class PrometheusOptions
{
    public const string ScrapePath = "/metrics";

    public bool Enabled { get; set; }
}

public sealed class LogFilesOptions
{
    public LogFileOptions App { get; set; } = new() { RetainedFileCountLimit = 14 };
    public LogFileOptions Http { get; set; } = new() { RetainedFileCountLimit = 7 };
}

public sealed class LogFileOptions
{
    public int RetainedFileCountLimit { get; set; } = 14;
    public long FileSizeLimitBytes { get; set; } = 100 * 1024 * 1024;

    internal void Validate(string name)
    {
        if (RetainedFileCountLimit <= 0)
            throw new InvalidOperationException($"Observability:Logs:{name}:RetainedFileCountLimit must be positive.");
        if (FileSizeLimitBytes <= 0)
            throw new InvalidOperationException($"Observability:Logs:{name}:FileSizeLimitBytes must be positive.");
    }
}
