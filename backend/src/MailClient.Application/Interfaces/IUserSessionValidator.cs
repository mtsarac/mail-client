using MailClient.Domain.Enums;

namespace MailClient.Application.Interfaces;

public sealed record ValidUserSession(Guid UserId, UserRole Role, int TokenVersion);

public interface IUserSessionValidator
{
    Task<ValidUserSession?> ValidateAsync(Guid userId, int tokenVersion, CancellationToken cancellationToken);
}
