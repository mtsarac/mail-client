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
        if (!TryHash(token, out var hash)) return null;
        var now = DateTime.UtcNow;
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            // Claim the token with a conditional UPDATE so two concurrent refreshes with the same
            // token produce exactly one replacement; the loser sees zero rows and is rejected.
            var claimed = await db.MailSessions
                .Where(x => x.RefreshTokenHash == hash && x.RevokedAt == null && x.ExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, now), cancellationToken);
            if (claimed != 1) return null;
            var session = await db.MailSessions.SingleAsync(x => x.RefreshTokenHash == hash, cancellationToken);
            session.LastUsedAt = now;
            var winner = await CreateAsync(session.MailAccountId, session.DeviceIdentifier, lifetime, cancellationToken);
            session.ReplacedBySessionId = winner.Session.Id;
            await db.SaveChangesAsync(cancellationToken);
            return winner;
        }

        var current = await db.MailSessions.SingleOrDefaultAsync(x => x.RefreshTokenHash == hash, cancellationToken);
        if (current is null || current.RevokedAt is not null || current.ExpiresAt <= now) return null;
        var replacement = await CreateAsync(current.MailAccountId, current.DeviceIdentifier, lifetime, cancellationToken);
        current.RevokedAt = now;
        current.LastUsedAt = now;
        current.ReplacedBySessionId = replacement.Session.Id;
        await db.SaveChangesAsync(cancellationToken);
        return replacement;
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken)
    {
        if (!TryHash(token, out var hash)) return false;
        var session = await db.MailSessions.SingleOrDefaultAsync(x => x.RefreshTokenHash == hash, cancellationToken);
        if (session is null || session.RevokedAt is not null) return false;
        session.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool TryHash(string? token, out string hash)
    {
        hash = "";
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            hash = RefreshTokenService.HashToken(token);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
