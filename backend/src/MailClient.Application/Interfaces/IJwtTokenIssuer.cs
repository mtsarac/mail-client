using MailClient.Domain.Enums;

namespace MailClient.Application.Interfaces;

public interface IJwtTokenIssuer
{
    string IssueToken(Guid userId, UserRole role, int tokenVersion);
}
