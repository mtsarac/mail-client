using Serilog;
using Serilog.Events;

namespace MailClient.Api.Logging;

public static class SerilogSetup
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var file = builder.Configuration.GetSection("Logging:File").Get<FileLogOptions>() ?? new FileLogOptions();
        var logDir = Path.IsPathFullyQualified(file.Directory)
            ? file.Directory
            : Path.Combine(builder.Environment.ContentRootPath, file.Directory);
        Directory.CreateDirectory(logDir);
        var json = new Serilog.Formatting.Compact.CompactJsonFormatter();

        builder.Host.UseSerilog((context, services, config) => config
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "MailClient")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(e => e.Properties.ContainsKey("SourceContext")
                    && e.Properties["SourceContext"].ToString().Contains("HttpLoggingMiddleware"))
                .WriteTo.File(
                    json,
                    Path.Combine(logDir, "app-.json"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: file.RetainedFileCount))
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("SourceContext")
                    && e.Properties["SourceContext"].ToString().Contains("HttpLoggingMiddleware"))
                .WriteTo.File(
                    json,
                    Path.Combine(logDir, "http-.json"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: Math.Max(3, file.RetainedFileCount / 2))));
    }
}
