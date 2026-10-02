using System.Security.Claims;
using FreeSpace.Domain.Identity;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Auth;

/// <summary>
/// Runs after the JWT signature/lifetime checks. Rejects tokens whose session was revoked,
/// whose tenant is no longer the session's active tenant, or whose user lost the membership,
/// and attaches the current role from the database.
/// </summary>
public static class SessionValidator
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity is not ClaimsIdentity identity
            || !Guid.TryParse(principal.FindFirstValue(FreeSpaceClaims.Subject), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(FreeSpaceClaims.Session), out var sessionId)
            || !Guid.TryParse(principal.FindFirstValue(FreeSpaceClaims.Tenant), out var tenantId))
        {
            context.Fail("Malformed access token.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var role = await (
            from s in db.Sessions
            join m in db.Memberships on new { s.UserId, s.TenantId } equals new { m.UserId, m.TenantId }
            join u in db.Users on s.UserId equals u.Id
            where s.Id == sessionId && s.UserId == userId && s.TenantId == tenantId
                  && s.RevokedAt == null && s.ExpiresAt > now
                  && u.Status == UserStatus.Active
            select (TenantRole?)m.Role
        ).FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (role is null)
        {
            context.Fail("Session is no longer valid.");
            return;
        }

        identity.AddClaim(new Claim(FreeSpaceClaims.TenantRole, role.Value.ToString()));
    }
}
