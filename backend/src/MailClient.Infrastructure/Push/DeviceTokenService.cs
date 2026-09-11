using MailClient.Application.Interfaces;
using MailClient.Application.Validation;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// Device-token registration with idempotent update and cross-user reassignment.
namespace MailClient.Infrastructure.Push;

public sealed class DeviceTokenService(
    AppDbContext db,
    ILogger<DeviceTokenService> logger) : IDeviceTokenService
{
    private static readonly HashSet<string> AllowedPlatforms = new(StringComparer.OrdinalIgnoreCase) { "android", "ios" };

    public async Task<DeviceTokenResponse> RegisterAsync(
        Guid userId, string pushToken, string platform, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var token = pushToken?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            errors["pushToken"] = ["Push token is required."];
        else if (token.Length > 500)
            errors["pushToken"] = ["Push token must be at most 500 characters."];
        var normalizedPlatform = platform?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPlatform))
            errors["platform"] = ["Platform is required."];
        else if (!AllowedPlatforms.Contains(normalizedPlatform))
            errors["platform"] = ["Platform must be one of: android, ios."];
        RequestValidator.ThrowIfInvalid(errors);

        var now = DateTime.UtcNow;
        var existing = await db.DeviceTokens.SingleOrDefaultAsync(
            device => device.Token == token, cancellationToken);
        if (existing is null)
        {
            var device = new DeviceToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = token,
                Platform = normalizedPlatform,
                RegisteredAt = now,
                LastSeenAt = now
            };
            db.DeviceTokens.Add(device);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolationFor(ex, "IX_DeviceTokens"))
            {
                db.ChangeTracker.Clear();
                return await ReassignAsync(userId, token, normalizedPlatform, now, cancellationToken);
            }

            logger.LogInformation(
                "Device registered for user {UserId} on {Platform}.", userId, normalizedPlatform);
            return ToResponse(device);
        }

        if (existing.UserId != userId)
            logger.LogInformation("Device token reassigned to user {UserId}.", userId);
        existing.UserId = userId;
        existing.Platform = normalizedPlatform;
        existing.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(existing);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await db.DeviceTokens.SingleOrDefaultAsync(
            item => item.Id == deviceId && item.UserId == userId, cancellationToken);
        if (device is null)
            return false;
        db.DeviceTokens.Remove(device);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Device {DeviceId} unregistered for user {UserId}.", deviceId, userId);
        return true;
    }

    private async Task<DeviceTokenResponse> ReassignAsync(
        Guid userId, string token, string platform, DateTime now, CancellationToken cancellationToken)
    {
        var existing = await db.DeviceTokens.SingleOrDefaultAsync(
            device => device.Token == token, cancellationToken);
        if (existing is null)
        {
            var device = new DeviceToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = token,
                Platform = platform,
                RegisteredAt = now,
                LastSeenAt = now
            };
            db.DeviceTokens.Add(device);
            await db.SaveChangesAsync(cancellationToken);
            return ToResponse(device);
        }

        existing.UserId = userId;
        existing.Platform = platform;
        existing.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(existing);
    }

    private static DeviceTokenResponse ToResponse(DeviceToken device) =>
        new(device.Id, device.Platform, device.RegisteredAt, device.LastSeenAt);
}
