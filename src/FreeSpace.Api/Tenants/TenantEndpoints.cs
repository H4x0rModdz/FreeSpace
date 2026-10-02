using System.ComponentModel.DataAnnotations;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Tenants;

public sealed record TenantNameRequest([Required, StringLength(200, MinimumLength = 1)] string Name);
public sealed record ChangeRoleRequest([Required] TenantRole Role);

public sealed record TenantSummary(Guid Id, string Name, TenantRole Role, DateTimeOffset JoinedAt);
public sealed record MemberResponse(Guid UserId, string Name, string Email, TenantRole Role, DateTimeOffset JoinedAt);

public static class TenantEndpoints
{
    public const string AdminPolicy = "TenantAdmin";

    public static void MapTenantEndpoints(this IEndpointRouteBuilder api)
    {
        var tenants = api.MapGroup("/tenants").WithTags("Tenants");
        tenants.MapGet("/", ListMine);
        tenants.MapPost("/", Create);
        tenants.MapGet("/current", GetCurrent);
        tenants.MapPatch("/current", Rename).RequireAuthorization(AdminPolicy);

        var members = tenants.MapGroup("/current/members");
        members.MapGet("/", ListMembers);
        members.MapPatch("/{userId:guid}", ChangeRole).RequireAuthorization(AdminPolicy);
        members.MapDelete("/{userId:guid}", RemoveMember); // members may remove themselves
    }

    private static async Task<IResult> ListMine(AppDbContext db, CurrentUser me, CancellationToken ct)
    {
        var userId = me.UserId;
        var result = await (
            from m in db.Memberships
            join t in db.Tenants on m.TenantId equals t.Id
            where m.UserId == userId
            orderby m.CreatedAt
            select new TenantSummary(t.Id, t.Name, m.Role, m.CreatedAt)
        ).ToListAsync(ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Create(TenantNameRequest request, AppDbContext db, CurrentUser me, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tenant = new Tenant(request.Name, now);
        var membership = new Membership(tenant.Id, me.UserId, TenantRole.Owner, now);
        db.AddRange(tenant, membership);
        audit.Record(tenant.Id, me.UserId, AuditActions.TenantCreated, "tenant", tenant.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/tenants/{tenant.Id}", new TenantSummary(tenant.Id, tenant.Name, membership.Role, membership.CreatedAt));
    }

    private static async Task<IResult> GetCurrent(AppDbContext db, CurrentUser me, CancellationToken ct)
    {
        var userId = me.UserId;
        var tenantId = me.RequiredTenantId;
        var result = await (
            from m in db.Memberships
            join t in db.Tenants on m.TenantId equals t.Id
            where m.UserId == userId && m.TenantId == tenantId
            select new TenantSummary(t.Id, t.Name, m.Role, m.CreatedAt)
        ).FirstAsync(ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Rename(TenantNameRequest request, AppDbContext db, CurrentUser me, AuditLog audit, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == me.RequiredTenantId, ct);
        var previous = tenant.Name;
        tenant.Rename(request.Name);
        audit.Record(tenant.Id, me.UserId, AuditActions.TenantRenamed, "tenant", tenant.Id, new { previous, current = tenant.Name });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListMembers(AppDbContext db, CurrentUser me, CancellationToken ct)
    {
        var tenantId = me.RequiredTenantId;
        var result = await (
            from m in db.Memberships
            join u in db.Users on m.UserId equals u.Id
            where m.TenantId == tenantId
            orderby m.CreatedAt
            select new MemberResponse(u.Id, u.Name, u.Email, m.Role, m.CreatedAt)
        ).ToListAsync(ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> ChangeRole(Guid userId, ChangeRoleRequest request, AppDbContext db, CurrentUser me, AuditLog audit, CancellationToken ct)
    {
        var tenantId = me.RequiredTenantId;
        var target = await db.Memberships.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);
        if (target is null) return MemberNotFound();
        if (target.Role == request.Role) return TypedResults.NoContent();

        if (!TenantPermissions.CanChangeRole(me.Role, target.Role, request.Role))
            return ApiErrors.Forbidden("insufficient_role", "Your role cannot grant or change this role.");
        if (target.Role == TenantRole.Owner && await IsLastOwner(db, tenantId, ct))
            return LastOwner();

        var previous = target.Role;
        target.ChangeRole(request.Role);
        audit.Record(tenantId, me.UserId, AuditActions.MemberRoleChanged, "user", userId, new { previous, current = request.Role });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RemoveMember(Guid userId, AppDbContext db, CurrentUser me, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var tenantId = me.RequiredTenantId;
        var target = await db.Memberships.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);
        if (target is null) return MemberNotFound();

        if (!TenantPermissions.CanRemove(me.Role, target.Role, isSelf: userId == me.UserId))
            return ApiErrors.Forbidden("insufficient_role", "Your role cannot remove this member.");
        if (target.Role == TenantRole.Owner && await IsLastOwner(db, tenantId, ct))
            return LastOwner();

        db.Memberships.Remove(target);
        // Sessions pointing at this tenant would fail validation anyway; revoke them explicitly.
        var now = clock.GetUtcNow();
        var sessions = await db.Sessions.Where(s => s.UserId == userId && s.TenantId == tenantId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in sessions) session.Revoke("membership_removed", now);

        audit.Record(tenantId, me.UserId, AuditActions.MemberRemoved, "user", userId, new { role = target.Role });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<bool> IsLastOwner(AppDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.Memberships.CountAsync(m => m.TenantId == tenantId && m.Role == TenantRole.Owner, ct) <= 1;

    private static IResult MemberNotFound() => ApiErrors.NotFound("member_not_found", "Member not found in this tenant.");
    private static IResult LastOwner() => ApiErrors.Conflict("last_owner", "A tenant must keep at least one owner.");
}
