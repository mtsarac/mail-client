using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailClient.Infrastructure.Observability;
using Microsoft.Extensions.Options;

namespace MailClient.Api.Observability;

public sealed class HttpLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxRequestBodyBytes { get; set; } = 65536;
    public int MaxResponseBodyBytes { get; set; } = 65536;
    public string[] ExcludedPaths { get; set; } = ["/health", "/metrics", "/swagger"];
}

internal sealed record CapturedBody(object? Value, bool Truncated)
{
    public static readonly CapturedBody Empty = new(null, false);
}

public sealed class HttpBodyLoggingMiddleware(
    RequestDelegate next,
    IOptions<HttpLoggingOptions> options,
    Serilog.ILogger logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var config = options.Value;
        if (!config.Enabled || IsExcluded(context.Request.Path.Value, config.ExcludedPaths))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var requestBody = await CaptureRequestAsync(context.Request, config);
        var capture = new CappedTeeStream(context.Response.Body, config.MaxResponseBodyBytes);
        context.Response.Body = capture;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = capture.Inner;
            stopwatch.Stop();
            LogCompletion(context, requestBody, capture, stopwatch.ElapsedMilliseconds);
        }
    }

    private static bool IsExcluded(string? path, string[] excluded) =>
        excluded.Any(prefix => (path ?? "/").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsJson(string? contentType) =>
        contentType is not null && (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                && contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private static async Task<CapturedBody> CaptureRequestAsync(HttpRequest request, HttpLoggingOptions config)
    {
        if (request.ContentLength == 0)
            return CapturedBody.Empty;
        var contentType = request.ContentType;
        if (contentType is not null && contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            return await CaptureMultipartAsync(request);
        if (!IsJson(contentType))
            return new CapturedBody(Metadata(contentType, request.ContentLength), false);
        request.EnableBuffering();
        var (bytes, truncated) = await ReadCappedAsync(request.Body, config.MaxRequestBodyBytes);
        request.Body.Position = 0;
        return BuildJsonBody(bytes, truncated);
    }

    private static async Task<CapturedBody> CaptureMultipartAsync(HttpRequest request)
    {
        try
        {
            request.EnableBuffering();
            var form = await request.ReadFormAsync();
            var attachments = form.Files
                .Select(file => (object?)new Dictionary<string, object?>
                {
                    ["fileName"] = file.FileName,
                    ["contentType"] = file.ContentType,
                    ["sizeBytes"] = file.Length,
                })
                .ToList();
            var meta = new Dictionary<string, object?>
            {
                ["contentType"] = request.ContentType,
                ["attachmentCount"] = attachments.Count,
                ["attachments"] = attachments,
                ["formFields"] = form.Select(field => (object?)field.Key).ToList(),
            };
            return new CapturedBody(meta, false);
        }
        catch
        {
            return new CapturedBody(Metadata(request.ContentType, request.ContentLength), false);
        }
    }

    private void LogCompletion(HttpContext context, CapturedBody requestBody, CappedTeeStream capture, long elapsedMs)
    {
        var responseBody = BuildResponseBody(context.Response.ContentType, capture);
        var correlationId = context.Response.Headers[CorrelationMiddleware.HeaderName].ToString();
        var path = Sanitize(context.Request.Path.Value ?? "/");
        var method = Sanitize(context.Request.Method);
        var query = context.Request.QueryString.Value;
        var log = logger
            .ForContext("RequestPath", path)
            .ForContext("Path", path)
            .ForContext("Method", method)
            .ForContext("StatusCode", context.Response.StatusCode)
            .ForContext("ElapsedMs", elapsedMs)
            .ForContext("CorrelationId", Sanitize(correlationId))
            .ForContext("TimestampUtc", DateTime.UtcNow)
            .ForContext("RequestBodyTruncated", requestBody.Truncated)
            .ForContext("ResponseBodyTruncated", responseBody.Truncated)
            .ForContext("RequestContentType", Sanitize(context.Request.ContentType))
            .ForContext("ResponseContentType", Sanitize(context.Response.ContentType))
            .ForContext("RequestBody", SanitizeBody(requestBody.Value), destructureObjects: true)
            .ForContext("ResponseBody", SanitizeBody(responseBody.Value), destructureObjects: true);
        if (!string.IsNullOrEmpty(query))
            log = log.ForContext("QueryString", Sanitize(query));
        log.Information(
            "HTTP {Method} {RequestPath} responded {StatusCode} in {ElapsedMs} ms.",
            method, path, context.Response.StatusCode, elapsedMs);
    }

    private static CapturedBody BuildResponseBody(string? contentType, CappedTeeStream capture)
    {
        if (capture.TotalBytes == 0)
            return CapturedBody.Empty;
        if (!IsJson(contentType))
            return new CapturedBody(Metadata(contentType, capture.TotalBytes), capture.Truncated);
        return BuildJsonBody(capture.Captured, capture.Truncated);
    }

    private static CapturedBody BuildJsonBody(byte[] bytes, bool truncated)
    {
        if (bytes.Length == 0)
            return CapturedBody.Empty;
        var text = Encoding.UTF8.GetString(bytes);
        try
        {
            var node = JsonNode.Parse(LogRedactor.Redact(text));
            MaskLongTextFields(node);
            return new CapturedBody(ToPlain(node), truncated);
        }
        catch
        {
            return truncated
                ? new CapturedBody($"[TRUNCATED JSON {bytes.Length} BYTES]", true)
                : new CapturedBody(Metadata("application/json", bytes.Length), false);
        }
    }

    private static Dictionary<string, object?> Metadata(string? contentType, long? lengthBytes) =>
        new()
        {
            ["contentType"] = contentType,
            ["lengthBytes"] = lengthBytes,
        };

    private static object? SanitizeBody(object? value) => value switch
    {
        string text => Sanitize(text),
        Dictionary<string, object?> dictionary => dictionary.ToDictionary(
            item => Sanitize(item.Key) ?? string.Empty,
            item => SanitizeBody(item.Value)),
        IEnumerable<object?> sequence => sequence.Select(SanitizeBody).ToList(),
        _ => value,
    };

    private static string? Sanitize(string? value) =>
        value?.Replace('\r', ' ').Replace('\n', ' ');

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(Stream stream, int maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var remaining = (long)maxBytes + 1;
        int read;
        while (remaining > 0
            && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)))) > 0)
        {
            buffer.Write(chunk, 0, read);
            remaining -= read;
        }

        var bytes = buffer.ToArray();
        return bytes.Length > maxBytes ? (bytes[..maxBytes], true) : (bytes, false);
    }

    private static void MaskLongTextFields(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(item => item.Key).ToArray())
            {
                if ((key.Equals("bodyHtml", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("bodyText", StringComparison.OrdinalIgnoreCase))
                    && obj[key] is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && text is not null)
                    obj[key] = $"[length:{text.Length} chars]";
                else
                    MaskLongTextFields(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                MaskLongTextFields(item);
        }
    }

    private static object? ToPlain(JsonNode? node) => node switch
    {
        JsonObject obj => obj.ToDictionary(item => item.Key, item => ToPlain(item.Value)),
        JsonArray array => array.Select(ToPlain).ToList(),
        JsonValue value => ToPlainValue(value),
        _ => null,
    };

    private static object? ToPlainValue(JsonValue value)
    {
        if (value.GetValueKind() == JsonValueKind.Null)
            return null;
        if (value.TryGetValue<string>(out var text))
            return text;
        if (value.TryGetValue<bool>(out var flag))
            return flag;
        if (value.TryGetValue<long>(out var integer))
            return integer;
        if (value.TryGetValue<double>(out var number))
            return number;
        if (value.TryGetValue<decimal>(out var precise))
            return precise;
        return value.ToString();
    }
}

internal sealed class CappedTeeStream(Stream inner, int maxBytes) : Stream
{
    private readonly MemoryStream _capture = new();

    public Stream Inner => inner;
    public long TotalBytes { get; private set; }
    public bool Truncated => TotalBytes > maxBytes;
    public byte[] Captured => _capture.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => TotalBytes;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Append(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        Append(buffer.Span);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _capture.Dispose();
        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        TotalBytes += data.Length;
        var room = (long)maxBytes - _capture.Length;
        if (room > 0)
            _capture.Write(data[..(int)Math.Min(room, data.Length)]);
    }
}
