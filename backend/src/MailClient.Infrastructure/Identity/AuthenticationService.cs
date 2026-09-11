using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Validation;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// Registration and login use cases with mode handling and race-safe duplicate mapping.
namespace MailClient.Infrastructure.Identity;

public sealed class AuthenticationService(
    AppDbContext db,
    IPasswordHasher<User> passwords,
    ILogger<AuthenticationService> logger) : IAuthenticationService
{
    public async Task<ServiceResult<RegisteredUser>> RegisterAsync(
        RegisterUserRequest request,
        string registrationMode,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        RequestValidator.RequireEmail(request?.Email, "email", errors);
        RequestValidator.RequirePassword(request?.Password, "password", errors);
        RequestValidator.RequireDisplayName(request?.DisplayName, "displayName", 250, errors);
        if (errors.Count > 0)
            return ServiceResult<RegisteredUser>.Failure(ServiceOutcome.Invalid, errors);

        if (string.Equals(registrationMode, "Disabled", StringComparison.OrdinalIgnoreCase))
            return ServiceResult<RegisteredUser>.Failure(ServiceOutcome.Invalid, "registration", "Registration is disabled.");

        var email = request!.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(user => user.Email == email, cancellationToken))
            return ServiceResult<RegisteredUser>.Failure(ServiceOutcome.Conflict, "email", "Email is already registered.");

        var user = new User
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            Status = string.Equals(registrationMode, "Open", StringComparison.OrdinalIgnoreCase)
                ? UserStatus.Active
                : UserStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = passwords.HashPassword(user, request.Password);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolationFor(ex, "IX_Users_Email"))
        {
            return ServiceResult<RegisteredUser>.Failure(ServiceOutcome.Conflict, "email", "Email is already registered.");
        }

        logger.LogInformation("User {UserId} registered with status {Status}.", user.Id, user.Status);
        return ServiceResult<RegisteredUser>.Success(new RegisteredUser(user.Id, user.Email, user.DisplayName, user.Status));
    }

    public async Task<ServiceResult<AuthenticatedUser>> LoginAsync(
        LoginUserRequest request,
        CancellationToken cancellationToken)
    {
        var email = request?.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(request?.Password))
            return ServiceResult<AuthenticatedUser>.Failure(ServiceOutcome.Unauthorized, "credentials", "Invalid email or password.");

        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);
        if (user is null || passwords.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            logger.LogWarning("Failed login attempt for {Email}.", email);
            return ServiceResult<AuthenticatedUser>.Failure(ServiceOutcome.Unauthorized, "credentials", "Invalid email or password.");
        }

        if (user.Status != UserStatus.Active)
        {
            logger.LogWarning("Login denied for {UserId} with status {Status}.", user.Id, user.Status);
            return ServiceResult<AuthenticatedUser>.Failure(ServiceOutcome.Forbidden, "status", "User account is not active.");
        }

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return ServiceResult<AuthenticatedUser>.Success(new AuthenticatedUser(user.Id, user.Email, user.Role, user.TokenVersion));
    }
}
