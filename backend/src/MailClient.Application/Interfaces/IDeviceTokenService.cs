namespace MailClient.Application.Interfaces;

public sealed record DeviceTokenResponse(
    Guid Id,
    string Platform,
    DateTime RegisteredAt,
    DateTime? LastSeenAt);

public interface IDeviceTokenService
{
    Task<DeviceTokenResponse> RegisterAsync(
        Guid userId, string pushToken, string platform, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
}
