using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;

namespace MailClient.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth").RequireRateLimiting("auth");

        group.MapPost("/register", async (RegisterRequest? request, IAuthenticationService auth, IConfiguration config, CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Request body is required."] });

            var result = await auth.RegisterAsync(
                new RegisterUserRequest(request.Email, request.Password, request.DisplayName),
                config["Registration:Mode"] ?? "ApprovalRequired",
                ct);

            return result.Outcome switch
            {
                // Security: identical response whether or not the address exists.
                ServiceOutcome.Ok or ServiceOutcome.Conflict => Results.Accepted(
                    "/api/auth/register",
                    new { message = "If the registration request can be accepted, it has been received." }),
                _ => Results.ValidationProblem(result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
            };
        }).AllowAnonymous();

        group.MapPost("/login", async (LoginRequest? request, IAuthenticationService auth, IJwtTokenIssuer tokens, CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Request body is required."] });

            var result = await auth.LoginAsync(new LoginUserRequest(request.Email, request.Password), ct);
            if (!result.Succeeded)
            {
                return result.Outcome switch
                {
                    ServiceOutcome.Forbidden => Results.Forbid(),
                    ServiceOutcome.Invalid => Results.ValidationProblem(result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value)),
                    _ => Results.Unauthorized()
                };
            }

            var user = result.Value!;
            var token = tokens.IssueToken(user.Id, user.Role, user.TokenVersion);
            return Results.Ok(new LoginResponse(token, user.Id, user.Email, user.Role));
        }).AllowAnonymous();

        return app;
    }
}

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, Guid UserId, string Email, UserRole Role);
