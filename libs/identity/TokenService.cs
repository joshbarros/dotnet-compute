using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Compute.Identity;

public record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Creates and hashes tokens. It knows nothing about the database, so it is a plain unit-test target.</summary>
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _o = options.Value;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(_o.Key));

    public AccessToken CreateAccessToken(Guid userId, string email, IEnumerable<string> roles)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_o.AccessMinutes);
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("email", email),
            new("jti", Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>48 random bytes, URL-safe. The raw value goes to the client once; only its hash is stored.</summary>
    public static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));

    public static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _o.Issuer,
        ValidateAudience = true,
        ValidAudience = _o.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        RequireExpirationTime = true,
        NameClaimType = "sub",
        RoleClaimType = "role",
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
    };
}
