using MailClient.Domain.Enums;

// JWT issuance seam used by the login endpoint.
namespace MailClient.Application.Interfaces;

public interface IJwtTokenIssuer
{
    string IssueToken(Guid userId, UserRole role, int tokenVersion);
}
