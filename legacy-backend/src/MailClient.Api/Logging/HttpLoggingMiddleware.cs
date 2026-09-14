using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

// Structured HTTP request/response logging with secret redaction and size
// limits. Bodies are parsed to JSON objects so logs stay jq-readable.
// Binary, multipart file bytes, and mail bodies are never logged raw.
namespace MailClient.Api.Logging;

public sealed class HttpLoggingMiddleware(
    RequestDelegate next,
    IOptions<HttpLoggingOptions> options,
    ILogger<HttpLoggingMiddleware> logger)
{
    private static readonly HashSet<string> SkippableContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/octet-stream", "image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf"
    };

    private readonly HttpLoggingOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || IsExcluded(context.Request.Path))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var requestCapture = await CaptureRequestAsync(context.Request);
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            context.Response.Body = originalBody;
        }

        var responseCapture = CaptureResponse(context.Response, buffer);
        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody);

        LogStructured(context, requestCapture, responseCapture, stopwatch.ElapsedMilliseconds);
    }

    private void LogStructured(
        HttpContext context,
        (string? ContentType, JsonNode? Body, bool Truncated, object? Meta) request,
        (string? ContentType, JsonNode? Body, bool Truncated) response,
        long elapsedMs)
    {
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = context.Items["CorrelationId"],
            ["Method"] = context.Request.Method,
            ["Path"] = context.Request.Path.Value,
            ["Query"] = context.Request.QueryString.Value,
            ["UserId"] = GetUserId(context.User),
            ["StatusCode"] = context.Response.StatusCode,
            ["ElapsedMs"] = elapsedMs,
            ["RequestContentType"] = request.ContentType,
            ["ResponseContentType"] = response.ContentType,
            ["Request"] = request.Meta ?? ToLogValue(request.Body),
            ["RequestTruncated"] = request.Truncated,
            ["Response"] = ToLogValue(response.Body),
            ["ResponseTruncated"] = response.Truncated,
            ["RemoteIp"] = context.Connection.RemoteIpAddress?.ToString()
        }))
        {
            logger.LogInformation("HTTP {Method} {Path} {StatusCode}", context.Request.Method, context.Request.Path.Value, context.Response.StatusCode);
        }
    }

    private async Task<(string? ContentType, JsonNode? Body, bool Truncated, object? Meta)> CaptureRequestAsync(HttpRequest request)
    {
        var contentType = request.ContentType?.Split(';')[0].Trim();
        if (request.ContentLength is 0 or null && !request.HasFormContentType)
            return (contentType, null, false, null);

        if (request.HasFormContentType)
            return (contentType, null, false, await SummarizeFormAsync(request));

        if (contentType is null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return (contentType, null, false, new { skipped = "non-json body" });

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var buffer = new char[Math.Min(_options.MaxRequestBodyBytes + 1, 1_048_576)];
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        request.Body.Position = 0;
        var truncated = read > _options.MaxRequestBodyBytes;
        var text = new string(buffer, 0, Math.Min(read, _options.MaxRequestBodyBytes));
        return (contentType, LogRedactor.ParseAndRedact(text), truncated, null);
    }

    private (string? ContentType, JsonNode? Body, bool Truncated) CaptureResponse(HttpResponse response, MemoryStream buffer)
    {
        var contentType = response.ContentType?.Split(';')[0].Trim();
        if (buffer.Length == 0 || contentType is null)
            return (contentType, null, false);
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || SkippableContentTypes.Contains(contentType))
            return (contentType, null, false);
        if (buffer.Length > _options.MaxResponseBodyBytes)
            return (contentType, null, true);
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, leaveOpen: true);
        return (contentType, LogRedactor.ParseAndRedact(reader.ReadToEnd()), false);
    }

    private static async Task<object?> SummarizeFormAsync(HttpRequest request)
    {
        try
        {
            request.EnableBuffering();
            var form = await request.ReadFormAsync();
            var fields = form
                .Where(field => !IsSensitiveField(field.Key))
                .ToDictionary(
                    field => field.Key,
                    field => field.Value.ToString().Length > 512
                        ? field.Value.ToString()[..512] + "...[truncated]"
                        : field.Value.ToString());
            var files = request.Form.Files
                .Select(file => new { file.FileName, file.ContentType, sizeBytes = file.Length })
                .ToList();
            return new { fields, files };
        }
        catch
        {
            return new { skipped = "unreadable form" };
        }
    }

    private static bool IsSensitiveField(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("bodyHtml", StringComparison.OrdinalIgnoreCase)
        || key.Contains("bodyText", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("credential", StringComparison.OrdinalIgnoreCase);

    private static object? ToLogValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var raw) => raw,
        JsonValue value when value.TryGetValue<long>(out var num) => num,
        JsonValue value when value.TryGetValue<double>(out var dbl) => dbl,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonObject obj => obj.ToDictionary(
            entry => entry.Key,
            entry => ToLogValue(entry.Value)),
        JsonArray array => array.Select(ToLogValue).ToList(),
        _ => node.ToJsonString()
    };

    private bool IsExcluded(PathString path) =>
        _options.ExcludedPaths.Any(excluded =>
            path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase));

    private static string? GetUserId(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(value, out _) ? value : null;
    }
}
