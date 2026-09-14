using System.Text;
using System.Text.Json;
using MailClient.Api.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailClient.Api.Tests;

public sealed class HttpLoggingMiddlewareTests
{
    [Fact]
    public async Task CorrelationId_Generated_WhenAbsent_AndEchoed()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);
        var echoed = context.Response.Headers["X-Correlation-ID"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(echoed));
        Assert.Equal(echoed, context.Items["CorrelationId"]);
    }

    [Fact]
    public async Task CorrelationId_UnsafeValue_Replaced()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers["X-Correlation-ID"] = "../../etc/passwd";
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);
        Assert.NotEqual("../../etc/passwd", context.Response.Headers["X-Correlation-ID"].ToString());
    }

    [Fact]
    public async Task RequestStream_RemainsReadable_AfterLogging()
    {
        var options = Options.Create(new HttpLoggingOptions());
        string? endpointBody = null;
        RequestDelegate next = async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            endpointBody = await reader.ReadToEndAsync();
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"ok":true}""");
        };
        var middleware = new HttpLoggingMiddleware(next, options, NullLogger<HttpLoggingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var json = """{"email":"a@b.c","password":"secret-1"}""";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.ContentLength = json.Length;
        context.Items["CorrelationId"] = "test-1";

        await middleware.InvokeAsync(context);

        Assert.Equal(json, endpointBody);
        context.Response.Body.Position = 0;
        var responseText = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal("""{"ok":true}""", responseText);
    }

    [Fact]
    public async Task OversizedBody_IsTruncated()
    {
        var options = Options.Create(new HttpLoggingOptions { MaxRequestBodyBytes = 16, MaxResponseBodyBytes = 16 });
        RequestDelegate next = async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"data":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        };
        var middleware = new HttpLoggingMiddleware(next, options, NullLogger<HttpLoggingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var json = """{"email":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","password":"x"}""";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.ContentLength = json.Length;
        context.Items["CorrelationId"] = "test-2";

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var responseText = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", responseText);
    }

    [Fact]
    public async Task ExcludedPath_SkipsLogging()
    {
        var options = Options.Create(new HttpLoggingOptions());
        var called = false;
        RequestDelegate next = ctx => { called = true; return Task.CompletedTask; };
        var middleware = new HttpLoggingMiddleware(next, options, NullLogger<HttpLoggingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/health";
        await middleware.InvokeAsync(context);
        Assert.True(called);
    }
}
