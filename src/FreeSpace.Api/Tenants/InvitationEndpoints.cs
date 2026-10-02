using System.ComponentModel.DataAnnotations;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
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

public static class InvitationEndpoints
{
    public static void MapInvitationEndpoints(this IEndpointRouteBuilder api)
    {
        var invitations = api.MapGroup("/tenants/current/invitations").WithTags("Invitations").RequireAuthorization(TenantEndpoints.AdminPolicy);
        invitations.MapGet("/", ListPending);
        invitations.MapPost("/", Create);
        invitations.MapDelete("/{id:guid}", Revoke);

        api.MapPost("/invitations/accept", Accept).WithTags("Invitations").RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }

    private static async Task<IResult> ListPending(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Tenant scoping comes from the global query filter.
        var result = await db.Invitations
            .Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationResponse(i.Id, i.Email, i.Role, i.CreatedAt, i.ExpiresAt))
            .ToListAsync(ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Create(
        CreateInvitationRequest request, AppDbContext db, CurrentUser me, AuditLog audit,
        IOptions<AuthOptions> authOptions, TimeProvider clock, CancellationToken ct)
    {
        if (!TenantPermissions.CanGrant(me.Role, request.Role))
            return ApiErrors.Forbidden("insufficient_role", "Your role cannot invite members with this role.");

        var now = clock.GetUtcNow();
        var token = SecureTokens.Generate();
        var invitation = new Invitation(me.RequiredTenantId, request.Email, request.Role, SecureTokens.Hash(token), me.UserId,
            now.AddDays(authOptions.Value.InvitationDays), now);
        db.Invitations.Add(invitation);
        audit.Record(invitation.TenantId, me.UserId, AuditActions.InvitationCreated, "invitation", invitation.Id, new { email = invitation.Email, role = invitation.Role });
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/v1/tenants/current/invitations/{invitation.Id}",
            new CreatedInvitationResponse(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, token));
    }

    private static async Task<IResult> Revoke(Guid id, AppDbContext db, CurrentUser me, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invitation is null) return ApiErrors.NotFound("invitation_not_found", "Invitation not found.");

        invitation.Revoke(clock.GetUtcNow());
        audit.Record(invitation.TenantId, me.UserId, AuditActions.InvitationRevoked, "invitation", invitation.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Accept(AcceptInvitationRequest request, AppDbContext db, CurrentUser me, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var tokenHash = SecureTokens.Hash(request.Token);
        // The invitation belongs to another tenant than the caller's active one: bypass the filter on purpose.
        var invitation = await db.Invitations.IgnoreQueryFilters([AppDbContext.TenantFilter])
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

        var now = clock.GetUtcNow();
        if (invitation is null || !invitation.IsPending(now))
            return ApiErrors.NotFound("invitation_invalid", "Invitation is invalid, expired or already used.");

        var userId = me.UserId;
        if (await db.Memberships.AnyAsync(m => m.TenantId == invitation.TenantId && m.UserId == userId, ct))
            return ApiErrors.Conflict("already_member", "You are already a member of this tenant.");

        invitation.Accept(userId, now);
        db.Memberships.Add(new Membership(invitation.TenantId, userId, invitation.Role, now));
        audit.Record(invitation.TenantId, userId, AuditActions.InvitationAccepted, "invitation", invitation.Id, new { role = invitation.Role });
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new AcceptedInvitationResponse(invitation.TenantId, invitation.Role));
    }
}
