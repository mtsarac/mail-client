using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MailClient.Application.Auth;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MailClient.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth").RequireRateLimiting("auth");

        group.MapPost("/register", async (RegisterRequest request, AppDbContext db, IPasswordHasher<User> passwords, IConfiguration config, CancellationToken ct) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = ["Email and password are required."] });

            try
            {
                PasswordPolicy.ValidateEmail(email);
                PasswordPolicy.ValidatePassword(request.Password);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = [ex.Message] });
            }

            if (await db.Users.AnyAsync(user => user.Email == email, ct))
                return Results.Conflict(new { error = "Email is already registered." });

            var mode = config["Registration:Mode"] ?? "ApprovalRequired";
            if (string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Registration is disabled." });

            var user = new User
            {
                Email = email,
                DisplayName = request.DisplayName.Trim(),
                Status = string.Equals(mode, "Open", StringComparison.OrdinalIgnoreCase) ? UserStatus.Active : UserStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            user.PasswordHash = passwords.HashPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/users/{user.Id}", new { user.Id, user.Email, user.DisplayName, user.Status });
        }).AllowAnonymous();

        group.MapPost("/login", async (LoginRequest request, AppDbContext db, IPasswordHasher<User> passwords, IOptions<JwtOptions> jwtOptions, CancellationToken ct) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Email == email, ct);
            if (user is null || passwords.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
                return Results.Unauthorized();
            if (user.Status != UserStatus.Active)
                return Results.Forbid();

            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new LoginResponse(CreateToken(user, jwtOptions.Value), user.Id, user.Email, user.Role));
        }).AllowAnonymous();

        return app;
    }

    private static string CreateToken(User user, JwtOptions options)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key));
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            [new(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new(ClaimTypes.Role, user.Role.ToString())],
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, Guid UserId, string Email, UserRole Role);
