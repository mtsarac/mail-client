// Bound from Logging:File and HttpLogging configuration sections.
namespace MailClient.Api.Logging;

public sealed class FileLogOptions
{
    public string Directory { get; set; } = "logs";
    public int RetainedFileCount { get; set; } = 14;
}

public sealed class HttpLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxRequestBodyBytes { get; set; } = 32 * 1024;
    public int MaxResponseBodyBytes { get; set; } = 64 * 1024;
    public string[] ExcludedPaths { get; set; } = ["/health", "/health/db", "/swagger", "/openapi"];
}
