namespace FreeSpace.Domain.Common;

/// <summary>
/// Marks an entity as belonging to a tenant. The persistence layer applies a global
/// query filter on <see cref="TenantId"/> for every type implementing this interface.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
