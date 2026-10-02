using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Identity;

/// <summary>
/// A login session. Holds the hash of the current refresh token and of the previous one,
/// so a replayed (already rotated) refresh token can be detected and the session revoked.
/// </summary>
public sealed class UserSession : Entity
{
    private UserSession() { }

    public UserSession(Guid userId, Guid tenantId, string refreshTokenHash, DateTimeOffset expiresAt, string? userAgent, string? ipAddress, DateTimeOffset now)
    {
        UserId = userId;
        TenantId = tenantId;
        RefreshTokenHash = refreshTokenHash;
        ExpiresAt = expiresAt;
        UserAgent = userAgent;
        IpAddress = ipAddress;
        CreatedAt = now;
    }

    public Guid UserId { get; private set; }
    /// <summary>Tenant currently active in this session.</summary>
    public Guid TenantId { get; private set; }
    public string RefreshTokenHash { get; private set; } = null!;
    public string? PreviousRefreshTokenHash { get; private set; }
    public string? UserAgent { get; private set; }
    public string? IpAddress { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastRefreshedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Rotate(string newRefreshTokenHash, DateTimeOffset newExpiresAt, DateTimeOffset now)
    {
        PreviousRefreshTokenHash = RefreshTokenHash;
        RefreshTokenHash = newRefreshTokenHash;
        ExpiresAt = newExpiresAt;
        LastRefreshedAt = now;
    }

    public void SwitchTenant(Guid tenantId) => TenantId = tenantId;

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (RevokedAt is not null) return;
        RevokedAt = now;
        RevokedReason = reason;
    }
}
