using System.Security.Cryptography;

namespace MailClient.Infrastructure.Authentication;

public static class RefreshTokenService
{
    public static string GenerateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(token)));
    public static bool FixedTimeEquals(string expectedHash, string token)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), SHA256.HashData(Convert.FromBase64String(token)));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
