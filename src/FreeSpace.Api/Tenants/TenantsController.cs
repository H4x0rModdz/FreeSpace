using System.ComponentModel.DataAnnotations;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Tenants;

public sealed record TenantNameRequest([Required, StringLength(200, MinimumLength = 1)] string Name);
public sealed record ChangeRoleRequest([Required] TenantRole Role);

public sealed record TenantSummary(Guid Id, string Name, TenantRole Role, DateTimeOffset JoinedAt);
public sealed record MemberResponse(Guid UserId, string Name, string Email, TenantRole Role, DateTimeOffset JoinedAt);

[Route("api/v1/tenants")]
[Tags("Tenants")]
public sealed class TenantsController(AppDbContext db, AuditLog audit, TimeProvider clock) : SecureController
{
    /// <summary>Tenants the caller belongs to.</summary>
    [HttpGet]
    [ProducesResponseType<List<TenantSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMine(CancellationToken ct)
    {
        var userId = UserId;
        var result = await (
            from m in db.Memberships
            join t in db.Tenants on m.TenantId equals t.Id
            where m.UserId == userId
            orderby m.CreatedAt
            select new TenantSummary(t.Id, t.Name, m.Role, m.CreatedAt)
        ).ToListAsync(ct);
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType<TenantSummary>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(TenantNameRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tenant = new Tenant(request.Name, now);
        var membership = new Membership(tenant.Id, UserId, TenantRole.Owner, now);
        db.AddRange(tenant, membership);
        audit.Record(tenant.Id, UserId, AuditActions.TenantCreated, "tenant", tenant.Id);
        await db.SaveChangesAsync(ct);
        return Created($"/api/v1/tenants/{tenant.Id}", new TenantSummary(tenant.Id, tenant.Name, membership.Role, membership.CreatedAt));
    }

    [HttpGet("current")]
    [ProducesResponseType<TenantSummary>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrent(CancellationToken ct)
    {
        var userId = UserId;
        var tenantId = TenantId;
        var result = await (
            from m in db.Memberships
            join t in db.Tenants on m.TenantId equals t.Id
            where m.UserId == userId && m.TenantId == tenantId
            select new TenantSummary(t.Id, t.Name, m.Role, m.CreatedAt)
        ).FirstAsync(ct);
        return Ok(result);
    }

    [HttpPatch("current"), MinimumRole(TenantRole.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Rename(TenantNameRequest request, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == TenantId, ct);
        var previous = tenant.Name;
        tenant.Rename(request.Name);
        audit.Record(tenant.Id, UserId, AuditActions.TenantRenamed, "tenant", tenant.Id, new { previous, current = tenant.Name });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("current/members")]
    [ProducesResponseType<List<MemberResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMembers(CancellationToken ct)
    {
        var tenantId = TenantId;
        var result = await (
            from m in db.Memberships
            join u in db.Users on m.UserId equals u.Id
            where m.TenantId == tenantId
            orderby m.CreatedAt
            select new MemberResponse(u.Id, u.Name, u.Email, m.Role, m.CreatedAt)
        ).ToListAsync(ct);
        return Ok(result);
    }

    [HttpPatch("current/members/{userId:guid}"), MinimumRole(TenantRole.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangeRole(Guid userId, ChangeRoleRequest request, CancellationToken ct)
    {
        var tenantId = TenantId;
        var target = await db.Memberships.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);
        if (target is null) return MemberNotFound();
        if (target.Role == request.Role) return NoContent();

        if (!TenantPermissions.CanChangeRole(Role, target.Role, request.Role))
            return InsufficientRole("Your role cannot grant or change this role.");
        if (target.Role == TenantRole.Owner && await IsLastOwner(tenantId, ct))
            return LastOwner();

        var previous = target.Role;
        target.ChangeRole(request.Role);
        audit.Record(tenantId, UserId, AuditActions.MemberRoleChanged, "user", userId, new { previous, current = request.Role });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Admins remove members below them; any member may remove themselves (leave).</summary>
    [HttpDelete("current/members/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveMember(Guid userId, CancellationToken ct)
    {
        var tenantId = TenantId;
        var target = await db.Memberships.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);
        if (target is null) return MemberNotFound();

        if (!TenantPermissions.CanRemove(Role, target.Role, isSelf: userId == UserId))
            return InsufficientRole("Your role cannot remove this member.");
        if (target.Role == TenantRole.Owner && await IsLastOwner(tenantId, ct))
            return LastOwner();

        db.Memberships.Remove(target);
        // Sessions pointing at this tenant would fail validation anyway; revoke them explicitly.
        var now = clock.GetUtcNow();
        var sessions = await db.Sessions.Where(s => s.UserId == userId && s.TenantId == tenantId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in sessions) session.Revoke("membership_removed", now);

        audit.Record(tenantId, UserId, AuditActions.MemberRemoved, "user", userId, new { role = target.Role });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<bool> IsLastOwner(Guid tenantId, CancellationToken ct) =>
        await db.Memberships.CountAsync(m => m.TenantId == tenantId && m.Role == TenantRole.Owner, ct) <= 1;

    private ObjectResult MemberNotFound() => NotFoundError("member_not_found", "Member not found in this tenant.");
    private ObjectResult LastOwner() => ConflictError("last_owner", "A tenant must keep at least one owner.");
}
