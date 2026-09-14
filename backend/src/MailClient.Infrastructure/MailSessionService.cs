using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Authentication;

public sealed class MailSessionService(AppDbContext db)
{
    public async Task<(MailSession Session, string Token)> CreateAsync(Guid accountId, string? deviceIdentifier, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var token = RefreshTokenService.GenerateToken();
        var now = DateTime.UtcNow;
        var session = new MailSession { Id = Guid.NewGuid(), MailAccountId = accountId, RefreshTokenHash = RefreshTokenService.HashToken(token), DeviceIdentifier = deviceIdentifier, CreatedAt = now, LastUsedAt = now, ExpiresAt = now.Add(lifetime) };
        db.MailSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return (session, token);
    }

    public async Task<(MailSession Session, string Token)?> RotateAsync(string token, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var hash = RefreshTokenService.HashToken(token);
        var current = await db.MailSessions.SingleOrDefaultAsync(x => x.RefreshTokenHash == hash, cancellationToken);
        if (current is null || current.RevokedAt is not null || current.ExpiresAt <= DateTime.UtcNow || !RefreshTokenService.FixedTimeEquals(current.RefreshTokenHash, token)) return null;
        var replacement = await CreateAsync(current.MailAccountId, current.DeviceIdentifier, lifetime, cancellationToken);
        current.RevokedAt = DateTime.UtcNow;
        current.LastUsedAt = current.RevokedAt.Value;
        current.ReplacedBySessionId = replacement.Session.Id;
        await db.SaveChangesAsync(cancellationToken);
        return replacement;
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken)
    {
        var hash = RefreshTokenService.HashToken(token);
        var session = await db.MailSessions.SingleOrDefaultAsync(x => x.RefreshTokenHash == hash, cancellationToken);
        if (session is null || session.RevokedAt is not null) return false;
        session.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
