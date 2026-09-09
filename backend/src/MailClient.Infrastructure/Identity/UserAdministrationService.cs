using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Validation;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Identity;

public sealed class UserAdministrationService(
    AppDbContext db,
    IPasswordHasher<User> passwords,
    ILogger<UserAdministrationService> logger) : IUserAdministrationService
{
    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken) =>
        await db.Users
            .OrderBy(user => user.Email)
            .Select(user => new UserDto(user.Id, user.Email, user.DisplayName, user.Role, user.Status))
            .ToListAsync(cancellationToken);

    public async Task<ServiceResult<UserDto>> CreateAsync(AdminCreateUserRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        RequestValidator.RequireEmail(request?.Email, "email", errors);
        RequestValidator.RequirePassword(request?.Password, "password", errors);
        RequestValidator.RequireDisplayName(request?.DisplayName, "displayName", 250, errors);
        if (errors.Count > 0)
            return ServiceResult<UserDto>.Failure(ServiceOutcome.Invalid, errors);

        var email = request!.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(user => user.Email == email, cancellationToken))
            return ServiceResult<UserDto>.Failure(ServiceOutcome.Conflict, "email", "Email is already registered.");

        var user = new User
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            Status = request.Status,
            Role = request.Role,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = passwords.HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Admin created user {UserId} with role {Role}.", user.Id, user.Role);
        return ServiceResult<UserDto>.Success(ToDto(user));
    }

    public async Task<ServiceResult<object?>> SetStatusAsync(Guid id, UserStatus status, CancellationToken cancellationToken)
    {
        var user = await db.Users.FindAsync([id], cancellationToken);
        if (user is null)
            return ServiceResult<object?>.Failure(ServiceOutcome.NotFound, "user", "User not found.");

        user.Status = status;
        user.TokenVersion++;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Admin set user {UserId} status to {Status}; sessions invalidated.", user.Id, status);
        return ServiceResult<object?>.Success(null);
    }

    public async Task<ServiceResult<object?>> ResetPasswordAsync(Guid id, ResetUserPasswordRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        RequestValidator.RequirePassword(request?.Password, "password", errors);
        if (errors.Count > 0)
            return ServiceResult<object?>.Failure(ServiceOutcome.Invalid, errors);

        var user = await db.Users.FindAsync([id], cancellationToken);
        if (user is null)
            return ServiceResult<object?>.Failure(ServiceOutcome.NotFound, "user", "User not found.");

        user.PasswordHash = passwords.HashPassword(user, request!.Password);
        user.TokenVersion++;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Admin reset password for user {UserId}; sessions invalidated.", user.Id);
        return ServiceResult<object?>.Success(null);
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.Role, user.Status);
}
