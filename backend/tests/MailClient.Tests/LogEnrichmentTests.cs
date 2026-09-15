using System.Security.Claims;
using MailClient.Api.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Core;

namespace MailClient.Tests;

public sealed class LogEnrichmentTests
{
    [Fact]
    public async Task AuthenticatedRequest_EnrichesLogWithMailAccountId()
    {
        var accountId = Guid.NewGuid();
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, accountId.ToString())
            ], "Test"))
        };

        var mw = new AuthenticatedLogEnrichmentMiddleware(async _ =>
        {
            logger.Information("test event");
            await Task.CompletedTask;
        });

        using (Serilog.Context.LogContext.PushProperty("TestMarker", "present"))
        {
            await mw.InvokeAsync(context);
        }

        var evt = Assert.Single(sink.Events);
        Assert.True(evt.Properties.ContainsKey("MailAccountId"), "MailAccountId must be present in authenticated log event");
        Assert.Equal($"\"{accountId}\"", evt.Properties["MailAccountId"].ToString());
    }

    [Fact]
    public async Task AnonymousRequest_EmitsNoMailAccountId()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var context = new DefaultHttpContext();

        var mw = new AuthenticatedLogEnrichmentMiddleware(async _ =>
        {
            logger.Information("test event");
            await Task.CompletedTask;
        });

        await mw.InvokeAsync(context);

        var evt = Assert.Single(sink.Events);
        Assert.False(evt.Properties.ContainsKey("MailAccountId"), "Anonymous request must not emit MailAccountId");
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
