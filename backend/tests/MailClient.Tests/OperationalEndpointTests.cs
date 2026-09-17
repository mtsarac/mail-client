using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Api.Endpoints;
using MailClient.Application.Sync;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MailClient.Tests;

public class NpgsqlApiFactory(string connectionString) : MailClientApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        });
    }
}

public sealed class UnavailableDatabaseApiFactory()
    : NpgsqlApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");

public sealed class UnwritableStorageApiFactory : MailClientApiFactory
{
    private readonly string _blockingFile = Path.GetTempFileName();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<LocalAttachmentStorage>();
            services.AddSingleton(new LocalAttachmentStorage(Path.Combine(_blockingFile, "data")));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        File.Delete(_blockingFile);
    }
}

public sealed class PrometheusApiFactory : MailClientApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Observability:Prometheus:Enabled", "true");
    }
}

public sealed class TelemetryDisabledApiFactory : MailClientApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Observability:Tracing:Enabled", "false");
        builder.UseSetting("Observability:Metrics:Enabled", "false");
    }
}

public sealed class HealthEndpointTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Live_ReturnsHealthyWithoutDependencyChecks()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("checks").EnumerateObject());
    }

    [Fact]
    public async Task Ready_ReportsDatabaseAndStorage()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal(
            ["postgres", "storage"],
            body.GetProperty("checks").EnumerateObject().Select(check => check.Name).Order().ToArray());
    }

    [Fact]
    public void Readiness_IncludesOnlyLocalInfrastructure()
    {
        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        Assert.Equal(
            ["postgres", "storage"],
            registrations.Where(registration => registration.Tags.Contains(HealthEndpoints.ReadyTag)).Select(registration => registration.Name).Order().ToArray());
    }

    [Fact]
    public async Task MetricsEndpoint_IsNotExposedByDefault()
    {
        var response = await factory.CreateClient().GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed class UnavailableDatabaseHealthTests(UnavailableDatabaseApiFactory factory) : IClassFixture<UnavailableDatabaseApiFactory>
{
    [Fact]
    public async Task Ready_DatabaseUnavailable_Returns503WithoutConnectionDetails()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", body.GetProperty("checks").GetProperty("postgres").GetString());
        Assert.DoesNotContain("Host", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Live_DatabaseUnavailable_StillReturns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public sealed class UnwritableStorageHealthTests(UnwritableStorageApiFactory factory) : IClassFixture<UnwritableStorageApiFactory>
{
    [Fact]
    public async Task Ready_StorageUnwritable_Returns503WithoutPath()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Healthy", body.GetProperty("checks").GetProperty("postgres").GetString());
        Assert.Equal("Unhealthy", body.GetProperty("checks").GetProperty("storage").GetString());
        Assert.DoesNotContain(Path.GetTempPath(), raw);
    }
}

public sealed class PrometheusEndpointTests(PrometheusApiFactory factory) : IClassFixture<PrometheusApiFactory>
{
    [Fact]
    public async Task MetricsEndpoint_WhenEnabled_ExposesMailClientMetricsWithoutIdentifiers()
    {
        var client = factory.CreateClient();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        await factory.Services.GetRequiredService<ISyncScheduler>()
            .ScheduleFolderAsync(accountId, folderId, SyncOrigin.UserRequested, CancellationToken.None);

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("mailclient_sync_scheduled", text);
        Assert.DoesNotContain(accountId.ToString(), text);
        Assert.DoesNotContain(folderId.ToString(), text);
    }
}

public sealed class TelemetryDisabledTests(TelemetryDisabledApiFactory factory) : IClassFixture<TelemetryDisabledApiFactory>
{
    [Fact]
    public async Task TelemetryDisabled_ApplicationStillServesRequests()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);
    }
}

[Collection("telemetry")]
public sealed class HttpTracingPrivacyTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task HttpSpans_DoNotCarryPathIdentifiersOrQueryText()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var mailId = Guid.NewGuid();

        await factory.CreateClient().GetAsync($"/api/mails/{mailId}?query=confidential-term");

        var span = Assert.Single(stopped);
        Assert.DoesNotContain(span.TagObjects, tag => tag.Key is "url.path" or "url.query" or "url.full");
        Assert.All(span.TagObjects, tag =>
        {
            Assert.DoesNotContain(mailId.ToString(), Convert.ToString(tag.Value)!);
            Assert.DoesNotContain("confidential", Convert.ToString(tag.Value)!);
        });
        Assert.DoesNotContain(mailId.ToString(), span.DisplayName);
    }
}

public sealed class OperationalEndpointRateLimitTests(PrometheusApiFactory factory) : IClassFixture<PrometheusApiFactory>
{
    // Exceeds two fixed 60-request windows, so a limited endpoint must hit 429 even if a window resets mid-test.
    private const int RequestCount = 125;

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    public async Task OperationalEndpoint_BypassesApiRateLimiter(string path)
    {
        var client = factory.CreateClient();

        for (var i = 0; i < RequestCount; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ApiEndpoint_RemainsSubjectToGlobalRateLimiter()
    {
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < RequestCount; i++)
            statuses.Add((await client.GetAsync("/api/mails")).StatusCode);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
