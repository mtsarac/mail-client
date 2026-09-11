using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Validates JWT sessions against live user status and token version.
namespace MailClient.Infrastructure.Identity;

public sealed class UserSessionValidator(AppDbContext db) : IUserSessionValidator
{
    public async Task<ValidUserSession?> ValidateAsync(Guid userId, int tokenVersion, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active || user.TokenVersion != tokenVersion)
            return null;

        return new ValidUserSession(user.Id, user.Role, user.TokenVersion);
    }
}
