using System.Text;
using FreeSpace.Api.Common;
using FreeSpace.Api.Configurations;
using FreeSpace.Domain.Identity;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FreeSpace.Api.Auth;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt)
{
    public string TokenType => "Bearer";
}

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private static readonly JsonWebTokenHandler Handler = new();

    public static SymmetricSecurityKey CreateSigningKey(JwtOptions jwt) => new(Encoding.UTF8.GetBytes(jwt.SigningKey));

    public AccessToken CreateAccessToken(Guid userId, Guid sessionId, Guid tenantId)
    {
        var jwt = options.Value;
        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(jwt.AccessTokenMinutes);
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(CreateSigningKey(jwt), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [FreeSpaceClaims.Subject] = userId.ToString(),
                [FreeSpaceClaims.Session] = sessionId.ToString(),
                [FreeSpaceClaims.Tenant] = tenantId.ToString(),
            },
        });
        return new AccessToken(token, expiresAt);
    }

    public DateTimeOffset RefreshTokenExpiry() => clock.GetUtcNow().AddDays(options.Value.RefreshTokenDays);

    /// <summary>
    /// Refresh tokens are <c>{sessionId:N}.{secret}</c>; only the secret's hash is stored, and the
    /// session id prefix lets us find the session without scanning by hash.
    /// </summary>
    public static (string Token, string SecretHash) NewRefreshToken(Guid sessionId)
    {
        var secret = SecureTokens.Generate();
        return ($"{sessionId:N}.{secret}", SecureTokens.Hash(secret));
    }

    public static bool TryParseRefreshToken(string token, out Guid sessionId, out string secretHash)
    {
        sessionId = Guid.Empty;
        secretHash = "";
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return false;
        if (!Guid.TryParseExact(token.AsSpan(0, dot), "N", out sessionId)) return false;
        secretHash = SecureTokens.Hash(token[(dot + 1)..]);
        return true;
    }
}

/// <summary>Creates and rotates sessions. Changes are staged on the DbContext; callers save.</summary>
public sealed class SessionIssuer(AppDbContext db, TokenService tokens, TimeProvider clock, IHttpContextAccessor accessor)
{
    public TokenPair Start(Guid userId, Guid tenantId)
    {
        var http = accessor.HttpContext;
        var expiresAt = tokens.RefreshTokenExpiry();
        // The entity id is generated client-side, so the token can embed it before saving.
        var session = new UserSession(userId, tenantId, refreshTokenHash: "", expiresAt, http?.UserAgent(), http?.IpAddress(), clock.GetUtcNow());
        var (refreshToken, secretHash) = TokenService.NewRefreshToken(session.Id);
        session.Rotate(secretHash, expiresAt, clock.GetUtcNow());
        db.Sessions.Add(session);

        var access = tokens.CreateAccessToken(userId, session.Id, tenantId);
        return new TokenPair(access.Token, access.ExpiresAt, refreshToken, expiresAt);
    }

    public TokenPair Rotate(UserSession session)
    {
        var expiresAt = tokens.RefreshTokenExpiry();
        var (refreshToken, secretHash) = TokenService.NewRefreshToken(session.Id);
        session.Rotate(secretHash, expiresAt, clock.GetUtcNow());
        var access = tokens.CreateAccessToken(session.UserId, session.Id, session.TenantId);
        return new TokenPair(access.Token, access.ExpiresAt, refreshToken, expiresAt);
    }
}
