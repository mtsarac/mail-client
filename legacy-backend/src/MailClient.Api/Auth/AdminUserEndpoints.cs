using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;

// Maps admin-only user lifecycle endpoints (list, create, approve, disable, reset password).
namespace MailClient.Api.Auth;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString())).WithTags("Admin Users");

        group.MapGet("/", async (IUserAdministrationService users, CancellationToken ct) =>
            Results.Ok(await users.ListAsync(ct)))
            .WithName("ListUsers")
            .WithSummary("List all users")
            .WithDescription("Admin only. Returns users ordered by email.")
            .Produces<IReadOnlyList<UserDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

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
        })
        .WithName("CreateUser")
        .WithSummary("Create a user as admin")
        .WithDescription("Admin only. Creates a user with the given role and status.")
        .Accepts<CreateUserRequest>("application/json")
        .Produces<UserDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        group.MapPatch("/{id:guid}/approve", (Guid id, IUserAdministrationService users, CancellationToken ct) =>
            SetStatus(id, UserStatus.Active, users, ct))
            .WithName("ApproveUser")
            .WithSummary("Approve a pending user")
            .WithDescription("Admin only. Sets status to Active and invalidates existing sessions.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        group.MapPatch("/{id:guid}/disable", (Guid id, IUserAdministrationService users, CancellationToken ct) =>
            SetStatus(id, UserStatus.Disabled, users, ct))
            .WithName("DisableUser")
            .WithSummary("Disable a user")
            .WithDescription("Admin only. Sets status to Disabled and invalidates existing sessions.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        group.MapPatch("/{id:guid}/enable", (Guid id, IUserAdministrationService users, CancellationToken ct) =>
            SetStatus(id, UserStatus.Active, users, ct))
            .WithName("EnableUser")
            .WithSummary("Re-enable a user")
            .WithDescription("Admin only. Sets status to Active and invalidates existing sessions.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

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
        })
        .WithName("ResetUserPassword")
        .WithSummary("Reset a user's password")
        .WithDescription("Admin only. Sets a new password and invalidates existing sessions.")
        .Accepts<ResetPasswordRequest>("application/json")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .ProducesValidationProblem();
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
