namespace FreeSpace.Domain.Tenancy;

/// <summary>
/// Role management rules. Owners manage everyone; admins manage (and grant) only roles below Admin.
/// The "at least one owner" invariant needs the owner count and is checked by the caller.
/// </summary>
public static class TenantPermissions
{
    public static bool CanManageMembers(TenantRole actor) => actor >= TenantRole.Admin;

    public static bool CanGrant(TenantRole actor, TenantRole role) =>
        actor == TenantRole.Owner || (actor == TenantRole.Admin && role < TenantRole.Admin);

    public static bool CanChangeRole(TenantRole actor, TenantRole currentRole, TenantRole newRole) =>
        CanGrant(actor, currentRole) && CanGrant(actor, newRole);

    public static bool CanRemove(TenantRole actor, TenantRole target, bool isSelf) =>
        isSelf || CanGrant(actor, target);
}
