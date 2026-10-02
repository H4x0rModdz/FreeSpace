namespace FreeSpace.Infrastructure.Persistence;

/// <summary>Provides the tenant of the current unit of work (request, job).</summary>
public interface ITenantContext
{
    /// <summary>Null when there is no tenant in scope; tenant-owned queries then return nothing.</summary>
    Guid? TenantId { get; }
}

public sealed class NoTenantContext : ITenantContext
{
    public static readonly NoTenantContext Instance = new();
    public Guid? TenantId => null;
}
