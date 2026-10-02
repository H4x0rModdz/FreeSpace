using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Tenancy;

/// <summary>
/// Links a user to a tenant. Intentionally not <see cref="ITenantOwned"/>: memberships are
/// what grants access to a tenant, so they are queried across tenants ("my tenants").
/// </summary>
public sealed class Membership : Entity
{
    private Membership() { }

    public Membership(Guid tenantId, Guid userId, TenantRole role, DateTimeOffset now)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public TenantRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeRole(TenantRole role) => Role = role;
}
