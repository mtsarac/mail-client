using MailClient.Domain.Enums;

namespace MailClient.Application.Interfaces;

public sealed record RegisterUserRequest(string Email, string Password, string DisplayName);

public sealed record LoginUserRequest(string Email, string Password);

public sealed record RegisteredUser(Guid Id, string Email, string DisplayName, UserStatus Status);

public sealed record AuthenticatedUser(Guid Id, string Email, UserRole Role, int TokenVersion);

public interface IAuthenticationService
{
    Task<ServiceResult<RegisteredUser>> RegisterAsync(
        RegisterUserRequest request,
        string registrationMode,
        CancellationToken cancellationToken);

    Task<ServiceResult<AuthenticatedUser>> LoginAsync(
        LoginUserRequest request,
        CancellationToken cancellationToken);
}
