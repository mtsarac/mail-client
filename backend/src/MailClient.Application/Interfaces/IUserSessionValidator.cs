using MailClient.Domain.Enums;

// Per-request session validation seam backing JWT token-version invalidation.
namespace MailClient.Application.Interfaces;

public sealed record ValidUserSession(Guid UserId, UserRole Role, int TokenVersion);

public interface IUserSessionValidator
{
    Task<ValidUserSession?> ValidateAsync(Guid userId, int tokenVersion, CancellationToken cancellationToken);
}
