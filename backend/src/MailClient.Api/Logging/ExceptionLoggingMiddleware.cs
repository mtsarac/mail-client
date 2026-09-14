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
            logger.LogError(
                ex,
                "Unhandled {ExceptionType} for authenticated request.",
                ex.GetType().FullName);
            throw;
        }
    }
}
