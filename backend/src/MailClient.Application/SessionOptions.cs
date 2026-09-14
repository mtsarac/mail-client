namespace MailClient.Application.Authentication;

public sealed class SessionOptions
{
    public int RefreshTokenLifetimeDays { get; init; } = 180;
    public bool SlidingExpiration { get; init; } = true;

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenLifetimeDays);

    public void Validate()
    {
        if (RefreshTokenLifetimeDays <= 0)
            throw new InvalidOperationException("Session:RefreshTokenLifetimeDays must be positive.");
    }
}
