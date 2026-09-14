using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using MailClient.Api.Observability;
using MailClient.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MailClient.Tests;

internal sealed class CapturingSink(ConcurrentBag<LogEvent> events) : ILogEventSink
{
    public void Emit(LogEvent logEvent) => events.Add(logEvent);
}

public sealed class HttpBodyLoggingTests : IDisposable
{
    private readonly ConcurrentBag<LogEvent> _events = new();

    public void Dispose() => Serilog.Log.CloseAndFlush();

    private HttpBodyLoggingMiddleware Create(RequestDelegate next, Action<HttpLoggingOptions>? configure = null)
    {
        var options = new HttpLoggingOptions();
        configure?.Invoke(options);
        var logger = new LoggerConfiguration().WriteTo.Sink(new CapturingSink(_events)).CreateLogger();
        return new HttpBodyLoggingMiddleware(next, Options.Create(options), logger);
    }

    private static DefaultHttpContext JsonContext(string method, string path, string json, string? query = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = query is null ? QueryString.Empty : new QueryString(query);
        context.Request.ContentType = "application/json; charset=utf-8";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        return context;
    }

    private static string Render(LogEvent evt) =>
        string.Join(" | ", evt.Properties.Select(kv => kv.Key + "=" + kv.Value));

    private LogEvent SingleEvent()
    {
        var evt = Assert.Single(_events);
        Assert.True(evt.Properties.ContainsKey("RequestPath"));
        return evt;
    }

    [Fact]
    public async Task JsonRequestBody_CapturedAsNestedObject()
    {
        var seenByEndpoint = "";
        var middleware = Create(async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            seenByEndpoint = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}");
        });
        var context = JsonContext("POST", "/api/accounts/discover", "{\"email\":\"person@example.com\"}");

        await middleware.InvokeAsync(context);

        Assert.Equal("{\"email\":\"person@example.com\"}", seenByEndpoint);
        var evt = SingleEvent();
        Assert.IsNotType<ScalarValue>(evt.Properties["RequestBody"]);
        Assert.Contains("person@example.com", evt.Properties["RequestBody"].ToString());
        Assert.Equal(false, ((ScalarValue)evt.Properties["RequestBodyTruncated"]).Value);
    }

    [Fact]
    public async Task JsonResponseBody_CapturedAsNestedObject()
    {
        var middleware = Create(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"mailAccountId\":\"abc\",\"safe\":true}");
        });
        var context = JsonContext("GET", "/api/account", "");

        await middleware.InvokeAsync(context);

        var evt = SingleEvent();
        Assert.IsNotType<ScalarValue>(evt.Properties["ResponseBody"]);
        Assert.Contains("abc", evt.Properties["ResponseBody"].ToString());
    }

    [Fact]
    public async Task NestedSecrets_RedactedCaseInsensitively()
    {
        var middleware = Create(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"items\":[{\"RefreshToken\":\"resp-secret\"}]}");
        });
        var context = JsonContext("POST", "/api/auth/refresh",
            "{\"nested\":{\"PASSWORD\":\"req-secret\"},\"list\":[{\"RefreshToken\":\"deep-secret\"}]}");

        await middleware.InvokeAsync(context);

        var rendered = Render(SingleEvent());
        Assert.DoesNotContain("req-secret", rendered);
        Assert.DoesNotContain("deep-secret", rendered);
        Assert.DoesNotContain("resp-secret", rendered);
        Assert.Contains("[REDACTED]", rendered);
    }

    [Fact]
    public async Task AuthorizationHeader_AndTokens_NeverCaptured()
    {
        var middleware = Create(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}");
        });
        var context = JsonContext("GET", "/api/mails", "{}");
        context.Request.Headers.Authorization = "Bearer jwt-secret-value";

        await middleware.InvokeAsync(context);

        var rendered = Render(SingleEvent());
        Assert.DoesNotContain("jwt-secret-value", rendered);
        Assert.DoesNotContain("Authorization=Bearer", rendered);
    }

    [Fact]
    public async Task LargeJsonBody_TruncatedWithFlags_AndEndpointStillReceivesFullBody()
    {
        var seenLength = 0;
        var middleware = Create(async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            seenLength = (await reader.ReadToEndAsync()).Length;
            context.Response.StatusCode = 200;
        }, options => options.MaxRequestBodyBytes = 16);
        var bigJson = "{\"email\":\"" + new string('a', 500) + "\"}";
        var context = JsonContext("POST", "/api/accounts/discover", bigJson);

        await middleware.InvokeAsync(context);

        Assert.Equal(bigJson.Length, seenLength);
        var evt = SingleEvent();
        Assert.Equal(true, ((ScalarValue)evt.Properties["RequestBodyTruncated"]).Value);
        Assert.DoesNotContain(new string('a', 500), Render(evt));
    }

    [Fact]
    public async Task MultipartSend_LogsMetadataOnly_NoBytesOrFieldValues()
    {
        const string boundary = "----testboundary";
        var binaryMarker = "BINARY-BYTES-12345";
        var htmlMarker = "<p>secret-html-body</p>";
        var raw = new StringBuilder()
            .Append("--" + boundary + "\r\nContent-Disposition: form-data; name=\"to\"\r\n\r\nfriend@example.test\r\n")
            .Append("--" + boundary + "\r\nContent-Disposition: form-data; name=\"subject\"\r\n\r\nHello\r\n")
            .Append("--" + boundary + "\r\nContent-Disposition: form-data; name=\"bodyHtml\"\r\n\r\n" + htmlMarker + "\r\n")
            .Append("--" + boundary + "\r\nContent-Disposition: form-data; name=\"attachment\"; filename=\"secret.pdf\"\r\nContent-Type: application/pdf\r\n\r\n" + binaryMarker + "\r\n")
            .Append("--" + boundary + "--\r\n")
            .ToString();
        var formReadable = false;
        var middleware = Create(async context =>
        {
            var form = await context.Request.ReadFormAsync();
            formReadable = form.Files.Count == 1 && form["to"] == "friend@example.test";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"sent\":true}");
        });
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/mails/send";
        context.Request.ContentType = "multipart/form-data; boundary=" + boundary;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        await middleware.InvokeAsync(context);

        Assert.True(formReadable);
        var rendered = Render(SingleEvent());
        Assert.DoesNotContain(binaryMarker, rendered);
        Assert.DoesNotContain(htmlMarker, rendered);
        Assert.DoesNotContain("friend@example.test", rendered);
        Assert.Contains("secret.pdf", rendered);
    }

    [Fact]
    public async Task BinaryResponse_NotCapturedAsBody()
    {
        var middleware = Create(async context =>
        {
            context.Response.ContentType = "application/octet-stream";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("BINARY-ATTACHMENT-BYTES"));
        });
        var context = JsonContext("GET", "/api/mails/11111111-1111-1111-1111-111111111111/attachments/22222222-2222-2222-2222-222222222222", "");

        await middleware.InvokeAsync(context);

        var rendered = Render(SingleEvent());
        Assert.DoesNotContain("BINARY-ATTACHMENT-BYTES", rendered);
    }

    [Fact]
    public async Task CorrelationId_AppearsInEvent_WithMailAccountAndQuery()
    {
        var accountId = Guid.NewGuid();
        var middleware = Create(async context =>
        {
            context.Response.StatusCode = 200;
            await Task.CompletedTask;
        });
        var context = JsonContext("GET", "/api/mails", "", "?folder=inbox");
        context.Items["CorrelationId"] = "corr-123";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, accountId.ToString())], "test"));

        await middleware.InvokeAsync(context);

        var evt = SingleEvent();
        Assert.Contains("corr-123", evt.Properties["CorrelationId"].ToString());
        Assert.Contains(accountId.ToString(), Render(evt));
        Assert.Contains("folder=inbox", Render(evt));
    }

    [Fact]
    public async Task AnonymousRequestWithoutQuery_OmitsOptionalProperties()
    {
        var middleware = Create(async context =>
        {
            context.Response.StatusCode = 200;
            await Task.CompletedTask;
        });
        var context = JsonContext("GET", "/api/accounts/discover", "");

        await middleware.InvokeAsync(context);

        var evt = SingleEvent();
        Assert.DoesNotContain("QueryString", Render(evt));
        Assert.DoesNotContain("MailAccountId", Render(evt));
    }

    [Fact]
    public async Task ExcludedPath_SkipsLogging()
    {
        var middleware = Create(async context =>
        {
            context.Response.StatusCode = 200;
            await Task.CompletedTask;
        });
        var context = JsonContext("GET", "/health", "");

        await middleware.InvokeAsync(context);

        Assert.Empty(_events);
    }

    [Fact]
    public async Task Disabled_SkipsLogging()
    {
        var middleware = Create(async context =>
        {
            context.Response.StatusCode = 200;
            await Task.CompletedTask;
        }, options => options.Enabled = false);
        var context = JsonContext("GET", "/api/mails", "{}");

        await middleware.InvokeAsync(context);

        Assert.Empty(_events);
    }

    [Theory]
    [InlineData("newPassword")]
    [InlineData("currentPassword")]
    [InlineData("authorization")]
    [InlineData("encryptedMaterial")]
    [InlineData("apiKey")]
    [InlineData("PASSWORD")]
    [InlineData("REFRESHTOKEN")]
    public void Redact_ExtendedKeys_RemovesSecret(string key)
    {
        var redacted = LogRedactor.Redact("{\"wrapper\":{\"" + key + "\":\"top-secret-value\",\"safe\":\"visible\"}}");
        Assert.DoesNotContain("top-secret-value", redacted);
        Assert.Contains("visible", redacted);
    }

    [Fact]
    public async Task NewlinesInCapturedValues_AreStrippedToPreventLogForging()
    {
        var middleware = Create(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}");
        });
        var context = JsonContext("POST", "/api/accounts/discover", "{\"note\":\"line1\\nline2\\rline3\"}");

        await middleware.InvokeAsync(context);

        var rendered = Render(SingleEvent());
        Assert.DoesNotContain("line1\nline2", rendered);
        Assert.DoesNotContain("line2\rline3", rendered);
        Assert.Contains("line1 line2 line3", rendered);
    }

    [Fact]
    public async Task ConnectManual_EndToEnd_RequestStreamUsable()
    {
        using var factory = new AcceptingApiFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/accounts/connect-manual",
            ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
