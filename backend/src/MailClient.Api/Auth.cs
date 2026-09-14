using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MailClient.Application.Accounts;
using Microsoft.IdentityModel.Tokens;

namespace MailClient.Api.Auth;

public sealed record JwtOptions(string Issuer, string Audience, string Key, int AccessTokenMinutes = 15);
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

public sealed class CurrentMailAccount(IHttpContextAccessor accessor) : ICurrentMailAccount
{
    public Guid MailAccountId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            var subject = user?.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user?.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(subject, out var id) ? id : throw new UnauthorizedAccessException();
        }
    }
}
