using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Storage;

/// <summary>
/// Pending OAuth authorization started by a tenant admin. Not <see cref="ITenantOwned"/>: the provider
/// callback arrives unauthenticated, so it is looked up by state hash and carries its own tenant/user.
/// </summary>
public sealed class OAuthState : Entity
{
    private OAuthState() { }

    public OAuthState(StorageProvider provider, string stateHash, Guid tenantId, Guid userId, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        Provider = provider;
        StateHash = stateHash;
        TenantId = tenantId;
        UserId = userId;
        ExpiresAt = expiresAt;
        CreatedAt = now;
    }

    public StorageProvider Provider { get; private set; }
    public string StateHash { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now;

    public void Consume(DateTimeOffset now) => ConsumedAt = now;
}
