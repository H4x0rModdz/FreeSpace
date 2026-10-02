using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Tests;

public sealed class TenantPermissionsTests
{
    [Theory]
    [InlineData(TenantRole.Owner, TenantRole.Owner, true)]
    [InlineData(TenantRole.Owner, TenantRole.Admin, true)]
    [InlineData(TenantRole.Admin, TenantRole.Member, true)]
    [InlineData(TenantRole.Admin, TenantRole.Admin, false)]
    [InlineData(TenantRole.Admin, TenantRole.Owner, false)]
    [InlineData(TenantRole.Member, TenantRole.Viewer, false)]
    public void CanGrant(TenantRole actor, TenantRole role, bool expected) =>
        Assert.Equal(expected, TenantPermissions.CanGrant(actor, role));

    [Theory]
    [InlineData(TenantRole.Admin, TenantRole.Member, TenantRole.Viewer, true)]
    [InlineData(TenantRole.Admin, TenantRole.Member, TenantRole.Admin, false)]
    [InlineData(TenantRole.Admin, TenantRole.Admin, TenantRole.Member, false)]
    [InlineData(TenantRole.Owner, TenantRole.Admin, TenantRole.Owner, true)]
    public void CanChangeRole(TenantRole actor, TenantRole current, TenantRole next, bool expected) =>
        Assert.Equal(expected, TenantPermissions.CanChangeRole(actor, current, next));

    [Theory]
    [InlineData(TenantRole.Viewer, TenantRole.Owner, true, true)]
    [InlineData(TenantRole.Member, TenantRole.Viewer, false, false)]
    [InlineData(TenantRole.Admin, TenantRole.Member, false, true)]
    [InlineData(TenantRole.Admin, TenantRole.Admin, false, false)]
    public void CanRemove(TenantRole actor, TenantRole target, bool isSelf, bool expected) =>
        Assert.Equal(expected, TenantPermissions.CanRemove(actor, target, isSelf));
}
