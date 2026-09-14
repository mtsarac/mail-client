using System.IdentityModel.Tokens.Jwt;
using MailClient.Application;
using Serilog.Context;

namespace MailClient.Api.Observability;

public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlation)
    {
        var supplied = context.Request.Headers[HeaderName];
        correlation.Initialize(supplied.Count == 1 && IsSafe(supplied[0])
            ? supplied[0]!.Trim()
            : Guid.NewGuid().ToString("N"));
        context.Response.Headers[HeaderName] = correlation.CorrelationId;
        using (LogContext.PushProperty("CorrelationId", correlation.CorrelationId))
        using (LogContext.PushProperty("MailAccountId", context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value))
        {
            await next(context);
        }
    }

    private static bool IsSafe(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxLength
        && value.Trim().All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
}
