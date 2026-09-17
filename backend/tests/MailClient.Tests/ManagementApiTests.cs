using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Api;
using MailClient.Api.Endpoints;
using MailClient.Application.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MailClient.Tests;

public sealed class ManagementApiFactory : MailClientApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ManagementOptions>();
            services.AddSingleton(new ManagementOptions
            {
                Enabled = true,
                ApiKey = "test-management-key-with-sufficient-length"
            });
        });
    }
}

public sealed class ManagementApiTests(ManagementApiFactory factory) : IClassFixture<ManagementApiFactory>
{
    private const string Path = "/api/management/runtime-settings/";

    [Fact]
    public async Task MissingOrWrongKey_IsRejected()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Management-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task CorrectKey_ReadsAndUpdatesWithOptimisticVersion()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Management-Key", "test-management-key-with-sufficient-length");
        var current = await client.GetFromJsonAsync<RuntimeSettingsResponse>(Path);
        Assert.NotNull(current);
        var settings = new RuntimeSettings
        {
            Search = new RuntimeSearchSettings { MaxPageSize = 25, MaxQueryLength = 100 }
        };

        var update = await client.PutAsJsonAsync(Path, new RuntimeSettingsUpdateRequest(current.Version, settings));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<RuntimeSettingsResponse>();
        Assert.Equal(current.Version + 1, updated!.Version);
        Assert.Equal(25, updated.Settings.Search.MaxPageSize);

        var stale = await client.PutAsJsonAsync(Path, new RuntimeSettingsUpdateRequest(current.Version, settings));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task MailAccountJwtWithoutManagementKey_IsRejected()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-management-credential");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
    }
}

public sealed class DisabledManagementApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task DisabledApi_IsNotFound()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Management-Key", "anything");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/management/runtime-settings/")).StatusCode);
    }
}
