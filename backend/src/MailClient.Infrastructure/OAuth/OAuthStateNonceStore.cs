using System.Security.Cryptography;
using System.Text;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.OAuth;

public interface IOAuthStateNonceStore
{
    Task<bool> TryConsumeAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

public sealed class OAuthStateNonceStore(AppDbContext db) : IOAuthStateNonceStore
{
    public async Task<bool> TryConsumeAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var expired = await db.OAuthStateNonces.Where(item => item.ExpiresAt <= now).ToListAsync(cancellationToken);
        db.OAuthStateNonces.RemoveRange(expired);
        db.OAuthStateNonces.Add(new OAuthStateNonce
        {
            Id = Guid.NewGuid(),
            NonceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))),
            ExpiresAt = expiresAt.UtcDateTime
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
