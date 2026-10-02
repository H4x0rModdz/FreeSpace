using System.ComponentModel.DataAnnotations;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Common;
using FreeSpace.Api.Configurations;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.Tenants;

public sealed record CreateInvitationRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required] TenantRole Role);

public sealed record AcceptInvitationRequest([Required, StringLength(256)] string Token);

public sealed record InvitationResponse(Guid Id, string Email, TenantRole Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>Returned once at creation; the raw token is never stored or shown again.</summary>
public sealed record CreatedInvitationResponse(Guid Id, string Email, TenantRole Role, DateTimeOffset ExpiresAt, string Token);

public sealed record AcceptedInvitationResponse(Guid TenantId, TenantRole Role);

[Route("api/v1/tenants/current/invitations")]
[Tags("Invitations")]
[MinimumRole(TenantRole.Admin)]
public sealed class InvitationsController(AppDbContext db, AuditLog audit, IOptions<AuthOptions> authOptions, TimeProvider clock) : SecureController
{
    /// <summary>Pending invitations of the active tenant.</summary>
    [HttpGet]
    [ProducesResponseType<List<InvitationResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPending(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Tenant scoping comes from the global query filter.
        var result = await db.Invitations
            .Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationResponse(i.Id, i.Email, i.Role, i.CreatedAt, i.ExpiresAt))
            .ToListAsync(ct);
        return Ok(result);
    }

    /// <summary>Creates a single-use invite link token. The token is only shown in this response.</summary>
    [HttpPost]
    [ProducesResponseType<CreatedInvitationResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateInvitationRequest request, CancellationToken ct)
    {
        if (!TenantPermissions.CanGrant(Role, request.Role))
            return InsufficientRole("Your role cannot invite members with this role.");

        var now = clock.GetUtcNow();
        var token = SecureTokens.Generate();
        var invitation = new Invitation(TenantId, request.Email, request.Role, SecureTokens.Hash(token), UserId,
            now.AddDays(authOptions.Value.InvitationDays), now);
        db.Invitations.Add(invitation);
        audit.Record(invitation.TenantId, UserId, AuditActions.InvitationCreated, "invitation", invitation.Id, new { email = invitation.Email, role = invitation.Role });
        await db.SaveChangesAsync(ct);

        return Created($"/api/v1/tenants/current/invitations/{invitation.Id}",
            new CreatedInvitationResponse(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, token));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invitation is null) return NotFoundError("invitation_not_found", "Invitation not found.");

        invitation.Revoke(clock.GetUtcNow());
        audit.Record(invitation.TenantId, UserId, AuditActions.InvitationRevoked, "invitation", invitation.Id);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Joins the inviting tenant. Does not switch the active tenant.</summary>
    [HttpPost("~/api/v1/invitations/accept"), MinimumRole(TenantRole.Viewer), EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType<AcceptedInvitationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Accept(AcceptInvitationRequest request, CancellationToken ct)
    {
        var tokenHash = SecureTokens.Hash(request.Token);
        // The invitation belongs to another tenant than the caller's active one: bypass the filter on purpose.
        var invitation = await db.Invitations.IgnoreQueryFilters([AppDbContext.TenantFilter])
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

        var now = clock.GetUtcNow();
        if (invitation is null || !invitation.IsPending(now))
            return NotFoundError("invitation_invalid", "Invitation is invalid, expired or already used.");

        var userId = UserId;
        if (await db.Memberships.AnyAsync(m => m.TenantId == invitation.TenantId && m.UserId == userId, ct))
            return ConflictError("already_member", "You are already a member of this tenant.");

        invitation.Accept(userId, now);
        db.Memberships.Add(new Membership(invitation.TenantId, userId, invitation.Role, now));
        audit.Record(invitation.TenantId, userId, AuditActions.InvitationAccepted, "invitation", invitation.Id, new { role = invitation.Role });
        await db.SaveChangesAsync(ct);

        return Ok(new AcceptedInvitationResponse(invitation.TenantId, invitation.Role));
    }
}
