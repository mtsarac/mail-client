using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MailClient.Application.Interfaces;
using MailClient.Application.Validation;

// Maps device-token endpoints for push registration and removal.
namespace MailClient.Api.Devices;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").RequireAuthorization().WithTags("Devices");

        group.MapPost("/register", async (
            DeviceRegisterRequest? request,
            ClaimsPrincipal user,
            IDeviceTokenService devices,
            CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["request"] = ["Request body is required."] });
            try
            {
                var device = await devices.RegisterAsync(GetUserId(user), request.PushToken, request.Platform, ct);
                return Results.Ok(device);
            }
            catch (RequestValidationException ex)
            {
                return Results.ValidationProblem(ex.Errors.ToDictionary(entry => entry.Key, entry => entry.Value));
            }
        }).RequireRateLimiting("mail-operations");

        group.MapDelete("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            IDeviceTokenService devices,
            CancellationToken ct) =>
            await devices.DeleteAsync(GetUserId(user), id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireRateLimiting("mail-operations");

        return app;
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(value, out var userId) ? userId : throw new UnauthorizedAccessException();
    }
}

public sealed record DeviceRegisterRequest(string PushToken, string Platform);
