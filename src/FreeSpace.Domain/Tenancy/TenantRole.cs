namespace FreeSpace.Domain.Tenancy;

/// <summary>Ordered from least to most privileged; comparisons rely on this order.</summary>
public enum TenantRole
{
    Viewer = 0,
    Member = 1,
    Admin = 2,
    Owner = 3,
}
