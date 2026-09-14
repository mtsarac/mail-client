using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Api.Tests;

public sealed class OpenApiMetadataTests(IntegrationFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public void ApiEndpoints_HaveNames()
    {
        using var scope = Fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>();
        var missing = sources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<EndpointNameMetadata>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null
                && !pattern.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
                && !pattern.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
        Assert.Empty(missing);
    }
}
