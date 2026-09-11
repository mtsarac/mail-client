using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

// Issues signed JWT access tokens carrying user id, role, and token version.
namespace MailClient.Api.Auth;

public sealed class JwtTokenIssuer(IOptions<JwtOptions> options) : IJwtTokenIssuer
{
    public const string TokenVersionClaim = "tv";

    public string IssueToken(Guid userId, UserRole role, int tokenVersion)
    {
        var settings = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key));
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            [
                new(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new(ClaimTypes.Role, role.ToString()),
                new(TokenVersionClaim, tokenVersion.ToString())
            ],
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
