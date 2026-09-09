using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;

namespace MailClient.Api.Auth;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString())).WithTags("Admin Users");

        group.MapGet("/", async (IUserAdministrationService users, CancellationToken ct) =>
            Results.Ok(await users.ListAsync(ct)));

        group.MapPost("/", async (CreateUserRequest? request, IUserAdministrationService users, CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Request body is required."] });

            var result = await users.CreateAsync(
                new AdminCreateUserRequest(request.Email, request.Password, request.DisplayName, request.Role, request.Status), ct);

            return result.Outcome switch
            {
                ServiceOutcome.Ok => Results.Created($"/api/admin/users/{result.Value!.Id}", result.Value),
                ServiceOutcome.Conflict => Results.Conflict(),
                _ => Results.ValidationProblem(result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
            };
        });

        group.MapPatch("/{id:guid}/approve", (Guid id, IUserAdministrationService users, CancellationToken ct) =>
            SetStatus(id, UserStatus.Active, users, ct));
        group.MapPatch("/{id:guid}/disable", (Guid id, IUserAdministrationService users, CancellationToken ct) =>
            SetStatus(id, UserStatus.Disabled, users, ct));
        group.MapPatch("/{id:guid}/enable", (Guid id, IUserAdministrationService users, CancellationToken ct) =>
            SetStatus(id, UserStatus.Active, users, ct));

        group.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest? request, IUserAdministrationService users, CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Request body is required."] });

            var result = await users.ResetPasswordAsync(id, new ResetUserPasswordRequest(request.Password), ct);
            return result.Outcome switch
            {
                ServiceOutcome.Ok => Results.NoContent(),
                ServiceOutcome.NotFound => Results.NotFound(),
                _ => Results.ValidationProblem(result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
            };
        });
        return app;
    }

    private static async Task<IResult> SetStatus(Guid id, UserStatus status, IUserAdministrationService users, CancellationToken ct)
    {
        var result = await users.SetStatusAsync(id, status, ct);
        return result.Outcome switch
        {
            ServiceOutcome.Ok => Results.NoContent(),
            ServiceOutcome.NotFound => Results.NotFound(),
            _ => Results.ValidationProblem(result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
        };
    }
}

public sealed record CreateUserRequest(string Email, string Password, string DisplayName, UserRole Role, UserStatus Status);
public sealed record ResetPasswordRequest(string Password);
