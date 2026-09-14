using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using Serilog.Context;

namespace MailClient.Api.Observability;

public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue("X-Correlation-ID", out var supplied) && supplied.Count == 1 && supplied[0]?.Length is > 0 and <= 64
            ? supplied[0]!
            : Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        var stopwatch = Stopwatch.StartNew();
        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("MailAccountId", context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value))
        {
            await next(context);
            logger.LogInformation("HTTP request responded {StatusCode} in {ElapsedMs} ms.", context.Response.StatusCode, stopwatch.ElapsedMilliseconds);
        }
    }
}
