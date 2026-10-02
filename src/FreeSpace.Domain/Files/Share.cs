using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Files;

/// <summary>
/// A public link to a file or folder. Anyone with the token can read it (and, for folders, everything
/// inside) until it expires or is revoked. Only the token hash is stored.
/// </summary>
public sealed class Share : Entity, ITenantOwned
{
    private Share() { }

    public Share(Guid tenantId, Guid nodeId, string tokenHash, Guid createdByUserId, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        TenantId = tenantId;
        NodeId = nodeId;
        TokenHash = tokenHash;
        CreatedByUserId = createdByUserId;
        ExpiresAt = expiresAt;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid NodeId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Null = until revoked.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
