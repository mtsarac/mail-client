using MailClient.Application;
using Serilog.Context;

// Accepts X-Correlation-ID (validated, max 64 chars) or generates one.
// Publishes it to the response, the ambient CorrelationContext, and Serilog.
namespace MailClient.Api.Logging;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsSafe(incoming) ? incoming.Trim() : Guid.NewGuid().ToString("N");
        context.Response.Headers[HeaderName] = correlationId;
        context.Items["CorrelationId"] = correlationId;
        CorrelationContext.Current = correlationId;
        using (LogContext.PushProperty("CorrelationId", correlationId))
            await next(context);
    }

    private static bool IsSafe(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
            return false;
        return value.Trim().All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
    }
}
