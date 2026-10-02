using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Auth;

/// <summary>Operations on the caller's own session and identity.</summary>
[Route("api/v1")]
[Tags("Auth")]
public sealed class SessionController(AppDbContext db, TokenService tokens, AuditLog audit, TimeProvider clock) : SecureController
{
    [HttpPost("auth/logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var session = await db.Sessions.FirstAsync(s => s.Id == SessionId, ct);
        session.Revoke("logout", clock.GetUtcNow());
        audit.Record(session.TenantId, UserId, AuditActions.Logout, "session", session.Id);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Switches the session's active tenant; access tokens for the previous tenant stop working.</summary>
    [HttpPost("auth/switch-tenant")]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SwitchTenant(SwitchTenantRequest request, CancellationToken ct)
    {
        if (!await db.Memberships.AnyAsync(m => m.UserId == UserId && m.TenantId == request.TenantId, ct))
            return ForbiddenError("not_a_member", "You are not a member of this tenant.");

        var session = await db.Sessions.FirstAsync(s => s.Id == SessionId, ct);
        session.SwitchTenant(request.TenantId);
        await db.SaveChangesAsync(ct);

        var access = tokens.CreateAccessToken(UserId, session.Id, request.TenantId);
        return Ok(new AccessTokenResponse(access.Token, access.ExpiresAt));
    }

    [HttpGet("me")]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = await db.Users.Where(u => u.Id == UserId)
            .Select(u => new MeUser(u.Id, u.Email, u.Name, u.EmailVerifiedAt != null))
            .FirstAsync(ct);
        var role = Role;
        var tenant = await db.Tenants.Where(t => t.Id == TenantId)
            .Select(t => new MeTenant(t.Id, t.Name, role))
            .FirstAsync(ct);
        return Ok(new MeResponse(user, tenant));
    }
}
