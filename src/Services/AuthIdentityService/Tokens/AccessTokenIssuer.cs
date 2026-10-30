using System.Security.Claims;
using AuthIdentityService.Data.Entities;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthIdentityService.Tokens;

/// <summary>Issues short-lived RS256 access tokens carrying `sub`, `role` and `permissions`.</summary>
public sealed class AccessTokenIssuer(AuthSigningKey key, KjJwtSettings settings, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    public string Issue(AuthUserEntity user, string sessionId, IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        var claims = new List<Claim>
        {
            new(KjClaims.Subject, user.Id),
            new(KjClaims.Email, user.Email ?? string.Empty),
            new(KjClaims.SessionId, sessionId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        claims.AddRange(roles.Select(role => new Claim(KjClaims.Role, role)));
        claims.AddRange(permissions.Distinct(StringComparer.Ordinal).Select(permission => new Claim(KjClaims.Permission, permission)));

        var now = time.GetUtcNow().UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(settings.AccessTokenLifetime),
            SigningCredentials = key.SigningCredentials
        });
    }
}
