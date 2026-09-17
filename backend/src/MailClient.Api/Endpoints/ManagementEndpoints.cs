using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailClient.Application;
using MailClient.Application.Runtime;
using MailClient.Infrastructure.Observability;

namespace MailClient.Api.Endpoints;

public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this WebApplication app)
    {
        var management = app.MapGroup("/api/management/runtime-settings").AddEndpointFilter<ManagementApiKeyFilter>();
        management.MapGet("/", async (IRuntimeSettingsStore store, CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetAsync(cancellationToken);
            return Results.Ok(new RuntimeSettingsResponse(snapshot.Settings, snapshot.Version, snapshot.UpdatedAt));
        });
        management.MapPut("/", async (RuntimeSettingsUpdateRequest request, IRuntimeSettingsStore store, AuditLogger audit, CorrelationContext correlation, CancellationToken cancellationToken) =>
        {
            var before = await store.GetAsync(cancellationToken);
            var after = await store.ReplaceAsync(request.ExpectedVersion, request.Settings, cancellationToken);
            await audit.WriteAsync(null, "runtime_settings_updated", "RuntimeConfiguration", "1", new
            {
                oldVersion = before.Version,
                newVersion = after.Version,
                changedSections = ChangedSections(before.Settings, after.Settings)
            }, correlation.CorrelationId, cancellationToken);
            return Results.Ok(new RuntimeSettingsResponse(after.Settings, after.Version, after.UpdatedAt));
        });
    }

    private static string[] ChangedSections(RuntimeSettings before, RuntimeSettings after)
    {
        var sections = new List<string>();
        if (!Same(before.Providers, after.Providers)) sections.Add("providers");
        if (!Same(before.Sync, after.Sync)) sections.Add("sync");
        if (!Same(before.Limits, after.Limits)) sections.Add("limits");
        if (!Same(before.Search, after.Search)) sections.Add("search");
        return [.. sections];

        static bool Same<T>(T beforeSection, T afterSection) =>
            JsonSerializer.Serialize(beforeSection) == JsonSerializer.Serialize(afterSection);
    }
}

public sealed record RuntimeSettingsUpdateRequest(int ExpectedVersion, RuntimeSettings Settings);
public sealed record RuntimeSettingsResponse(RuntimeSettings Settings, int Version, DateTime UpdatedAt);

public sealed class ManagementApiKeyFilter(ManagementOptions options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!options.Enabled)
        {
            return ValueTask.FromResult<object?>(Results.NotFound());
        }

        var supplied = context.HttpContext.Request.Headers["X-Management-Key"].ToString();
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash))
        {
            return ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status401Unauthorized, extensions: new Dictionary<string, object?> { ["code"] = "management_unauthorized" }));
        }

        return next(context);
    }
}
