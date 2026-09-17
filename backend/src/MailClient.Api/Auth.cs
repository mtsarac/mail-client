using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MailClient.Application.Accounts;
using Microsoft.IdentityModel.Tokens;

namespace MailClient.Api.Auth;

public sealed record JwtOptions(string Issuer, string Audience, string Key, int AccessTokenMinutes = 15)
{
    public const string DevelopmentKey = "development-only-key-change-before-production-123456789";
}
public sealed class JwtTokenIssuer(JwtOptions options) : IJwtTokenIssuer
{
    public (string Token, DateTime ExpiresAt) Issue(Guid mailAccountId)
    {
        var expires = DateTime.UtcNow.AddMinutes(options.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor { Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, mailAccountId.ToString())]), Issuer = options.Issuer, Audience = options.Audience, Expires = expires, SigningCredentials = new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)), SecurityAlgorithms.HmacSha256) };
        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(handler.CreateToken(descriptor)), expires);
    }
}

/// <summary>
/// Single source of truth for reading the authenticated MailAccount id. JWT inbound claim mapping is
/// disabled (see Program.cs), so the account id is always the raw "sub" claim.
/// </summary>
public static class MailAccountClaims
{
    public const string AccountIdClaimType = JwtRegisteredClaimNames.Sub;

    public static bool TryGetAccountId(ClaimsPrincipal? principal, out Guid mailAccountId)
    {
        mailAccountId = Guid.Empty;
        return principal?.Identity?.IsAuthenticated == true
            && Guid.TryParse(principal.FindFirstValue(AccountIdClaimType), out mailAccountId);
    }

    public static string? GetAccountIdOrNull(ClaimsPrincipal? principal) =>
        TryGetAccountId(principal, out var mailAccountId) ? mailAccountId.ToString() : null;
}

public sealed class CurrentMailAccount(IHttpContextAccessor accessor) : ICurrentMailAccount
{
    public Guid MailAccountId => MailAccountClaims.TryGetAccountId(accessor.HttpContext?.User, out var id)
        ? id
        : throw new UnauthorizedAccessException();
}
