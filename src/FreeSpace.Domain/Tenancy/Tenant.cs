using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Tenancy;

/// <summary>How new uploads pick a storage account among those with enough free space.</summary>
public enum UploadRoutingPolicy
{
    /// <summary>The account with the most free space (accounts without a limit first).</summary>
    MostAvailable,
    /// <summary>Rotate through accounts in priority order.</summary>
    RoundRobin,
    /// <summary>Always the lowest priority number that fits.</summary>
    Priority,
}

public sealed class Tenant : Entity
{
    private Tenant() { }

    public Tenant(string name, DateTimeOffset now)
    {
        Name = name.Trim();
        CreatedAt = now;
    }

    public string Name { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public UploadRoutingPolicy UploadRoutingPolicy { get; private set; } = UploadRoutingPolicy.MostAvailable;
    /// <summary>Advanced atomically by the allocator for <see cref="UploadRoutingPolicy.RoundRobin"/>.</summary>
    public int RoundRobinCursor { get; private set; }

    public void Rename(string name) => Name = name.Trim();

    public void SetUploadRoutingPolicy(UploadRoutingPolicy policy) => UploadRoutingPolicy = policy;
}
