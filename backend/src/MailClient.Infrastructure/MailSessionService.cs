using MailClient.Application.Authentication;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Authentication;

public sealed class MailSessionService(AppDbContext db, SessionOptions options)
{
    public Task<(MailSession Session, string Token)> CreateAsync(Guid accountId, string? deviceIdentifier, CancellationToken cancellationToken) =>
        CreateAsync(accountId, deviceIdentifier, options.RefreshTokenLifetime, cancellationToken);

    public async Task<(MailSession Session, string Token)> CreateAsync(Guid accountId, string? deviceIdentifier, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var token = RefreshTokenService.GenerateToken();
        var now = DateTime.UtcNow;
        var session = new MailSession { Id = Guid.NewGuid(), MailAccountId = accountId, RefreshTokenHash = RefreshTokenService.HashToken(token), DeviceIdentifier = deviceIdentifier, CreatedAt = now, LastUsedAt = now, ExpiresAt = now.Add(lifetime) };
        db.MailSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return (session, token);
    }

    public Task<(MailSession Session, string Token)?> RotateAsync(string token, CancellationToken cancellationToken) =>
        RotateAsync(token, options.RefreshTokenLifetime, options.SlidingExpiration, cancellationToken);

    public async Task<(MailSession Session, string Token)?> RotateAsync(string token, TimeSpan lifetime, bool slide, CancellationToken cancellationToken)
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
            var winner = await CreateAsync(session.MailAccountId, session.DeviceIdentifier, RenewedLifetime(session, lifetime, slide, now), cancellationToken);
            session.ReplacedBySessionId = winner.Session.Id;
            await db.SaveChangesAsync(cancellationToken);
            return winner;
        }

        var current = await db.MailSessions.SingleOrDefaultAsync(x => x.RefreshTokenHash == hash, cancellationToken);
        if (current is null || current.RevokedAt is not null || current.ExpiresAt <= now) return null;
        var replacement = await CreateAsync(current.MailAccountId, current.DeviceIdentifier, RenewedLifetime(current, lifetime, slide, now), cancellationToken);
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

    // Sliding rotation renews from the refresh instant; a fixed lifetime keeps the
    // original absolute expiry so a session can never outlive its initial bound.
    private static TimeSpan RenewedLifetime(MailSession current, TimeSpan lifetime, bool slide, DateTime now) =>
        slide ? lifetime : current.ExpiresAt - now;

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
