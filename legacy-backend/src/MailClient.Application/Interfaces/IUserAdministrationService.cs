using MailClient.Domain.Enums;

// Admin user-management seam: listing, creation, status changes, and password resets.
namespace MailClient.Application.Interfaces;

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role, UserStatus Status);

public sealed record AdminCreateUserRequest(string Email, string Password, string DisplayName, UserRole Role, UserStatus Status);

public sealed record ResetUserPasswordRequest(string Password);

public interface IUserAdministrationService
{
    Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken);

    Task<ServiceResult<UserDto>> CreateAsync(AdminCreateUserRequest request, CancellationToken cancellationToken);

    Task<ServiceResult<object?>> SetStatusAsync(Guid id, UserStatus status, CancellationToken cancellationToken);

    Task<ServiceResult<object?>> ResetPasswordAsync(Guid id, ResetUserPasswordRequest request, CancellationToken cancellationToken);
}
