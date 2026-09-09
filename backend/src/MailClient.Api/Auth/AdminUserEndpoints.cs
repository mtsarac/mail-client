using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Auth;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString())).WithTags("Admin Users");
        group.MapGet("/", async (AppDbContext db, CancellationToken ct) => await db.Users.OrderBy(user => user.Email).Select(user => new UserResponse(user.Id, user.Email, user.DisplayName, user.Role, user.Status)).ToListAsync(ct));
        group.MapPost("/", async (CreateUserRequest request, AppDbContext db, IPasswordHasher<User> passwords, CancellationToken ct) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(user => user.Email == email, ct)) return Results.Conflict();
            var user = new User { Email = email, DisplayName = request.DisplayName.Trim(), Status = request.Status, Role = request.Role, CreatedAt = DateTime.UtcNow };
            user.PasswordHash = passwords.HashPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/users/{user.Id}", new UserResponse(user.Id, user.Email, user.DisplayName, user.Role, user.Status));
        });
        group.MapPatch("/{id:guid}/approve", (Guid id, AppDbContext db, CancellationToken ct) => SetStatus(id, UserStatus.Active, db, ct));
        group.MapPatch("/{id:guid}/disable", (Guid id, AppDbContext db, CancellationToken ct) => SetStatus(id, UserStatus.Disabled, db, ct));
        group.MapPatch("/{id:guid}/enable", (Guid id, AppDbContext db, CancellationToken ct) => SetStatus(id, UserStatus.Active, db, ct));
        group.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest request, AppDbContext db, IPasswordHasher<User> passwords, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([id], ct);
            if (user is null) return Results.NotFound();
            user.PasswordHash = passwords.HashPassword(user, request.Password);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        return app;
    }

    private static async Task<IResult> SetStatus(Guid id, UserStatus status, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound();
        user.Status = status;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

public sealed record CreateUserRequest(string Email, string Password, string DisplayName, UserRole Role, UserStatus Status);
public sealed record ResetPasswordRequest(string Password);
public sealed record UserResponse(Guid Id, string Email, string DisplayName, UserRole Role, UserStatus Status);
