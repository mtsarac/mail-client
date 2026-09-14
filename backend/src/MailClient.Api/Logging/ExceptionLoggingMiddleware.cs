using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

// Logs unhandled exceptions once with full diagnostics, then rethrows so the
// built-in exception handler still renders ProblemDetails without stack traces.
namespace MailClient.Api.Logging;

public sealed class ExceptionLoggingMiddleware(RequestDelegate next, ILogger<ExceptionLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var user = context.User;
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
            logger.LogError(
                ex,
                "Unhandled {ExceptionType} on {Method} {Path} for user {UserId} correlation {CorrelationId}",
                ex.GetType().FullName,
                context.Request.Method,
                context.Request.Path.Value,
                Guid.TryParse(userId, out _) ? userId : null,
                context.Items["CorrelationId"]);
            throw;
        }
    }
}
