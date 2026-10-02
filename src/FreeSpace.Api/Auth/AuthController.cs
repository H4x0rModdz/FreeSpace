using System.ComponentModel.DataAnnotations;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Api.Configurations;
using FreeSpace.Domain.Identity;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using DomainUser = FreeSpace.Domain.Identity.User;

namespace FreeSpace.Api.Auth;

public sealed record RegisterRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(128, MinimumLength = 10)] string Password,
    [StringLength(200, MinimumLength = 1)] string? TenantName);

public sealed record LoginRequest(
    [Required, StringLength(320)] string Email,
    [Required, StringLength(128)] string Password,
    Guid? TenantId);

public sealed record RefreshRequest([Required, StringLength(512)] string RefreshToken);

public sealed record SwitchTenantRequest([Required] Guid TenantId);

public sealed record AccessTokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt)
{
    public string TokenType => "Bearer";
}

public sealed record MeResponse(MeUser User, MeTenant Tenant);
public sealed record MeUser(Guid Id, string Email, string Name, bool EmailVerified);
public sealed record MeTenant(Guid Id, string Name, TenantRole Role);

/// <summary>Anonymous endpoints that establish a session.</summary>
[Route("api/v1/auth")]
[Tags("Auth")]
public sealed class AuthController(
    AppDbContext db, IPasswordHasher hasher, SessionIssuer sessions, AuditLog audit,
    IOptions<AuthOptions> authOptions, TimeProvider clock) : BaseController
{
    /// <summary>Creates a user plus a personal tenant they own, and signs them in.</summary>
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType<TokenPair>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        if (!authOptions.Value.AllowRegistration)
            return ForbiddenError("registration_disabled", "Self-service registration is disabled.");

        var normalizedEmail = DomainUser.NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct))
            return EmailTaken();

        var now = clock.GetUtcNow();
        var user = new DomainUser(request.Email, request.Name, hasher.Hash(request.Password), now);
        var tenant = new Tenant(string.IsNullOrWhiteSpace(request.TenantName) ? $"{user.Name}'s space" : request.TenantName, now);
        db.AddRange(user, tenant, new Membership(tenant.Id, user.Id, TenantRole.Owner, now));
        var pair = sessions.Start(user.Id, tenant.Id);
        audit.Record(tenant.Id, user.Id, AuditActions.UserRegistered, "user", user.Id);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return EmailTaken(); // lost a race with a concurrent registration
        }

        return Created("/api/v1/me", pair);
    }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType<TokenPair>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var normalizedEmail = DomainUser.NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct);
        if (user?.PasswordHash is null)
        {
            hasher.SimulateVerify(request.Password); // same timing whether or not the e-mail exists
            return InvalidCredentials();
        }
        if (!hasher.Verify(user.PasswordHash, request.Password)) return InvalidCredentials();
        if (!user.IsActive) return ForbiddenError("account_disabled", "This account is disabled.");

        var memberships = db.Memberships.Where(m => m.UserId == user.Id);
        Guid tenantId;
        if (request.TenantId is { } requested)
        {
            if (!await memberships.AnyAsync(m => m.TenantId == requested, ct))
                return ForbiddenError("not_a_member", "You are not a member of this tenant.");
            tenantId = requested;
        }
        else
        {
            var first = await memberships.OrderBy(m => m.CreatedAt).Select(m => (Guid?)m.TenantId).FirstOrDefaultAsync(ct);
            if (first is null)
            {
                // Every user needs somewhere to land; recreate a personal space if they left all tenants.
                var now = clock.GetUtcNow();
                var personal = new Tenant($"{user.Name}'s space", now);
                db.AddRange(personal, new Membership(personal.Id, user.Id, TenantRole.Owner, now));
                audit.Record(personal.Id, user.Id, AuditActions.TenantCreated, "tenant", personal.Id);
                first = personal.Id;
            }
            tenantId = first.Value;
        }

        var pair = sessions.Start(user.Id, tenantId);
        audit.Record(tenantId, user.Id, AuditActions.Login, "user", user.Id);
        await db.SaveChangesAsync(ct);
        return Ok(pair);
    }

    /// <summary>Rotates the refresh token. Presenting an already-rotated token revokes the whole session.</summary>
    [HttpPost("refresh"), AllowAnonymous, EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType<TokenPair>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        if (!TokenService.TryParseRefreshToken(request.RefreshToken, out var sessionId, out var secretHash))
            return InvalidRefreshToken();

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return InvalidRefreshToken();

        var now = clock.GetUtcNow();
        if (session.PreviousRefreshTokenHash is { } previous && SecureTokens.HashEquals(previous, secretHash))
        {
            // An already-rotated token came back: assume it leaked and kill the whole session.
            if (session.IsActive(now))
            {
                session.Revoke("refresh_token_reused", now);
                audit.Record(session.TenantId, session.UserId, AuditActions.RefreshTokenReused, "session", session.Id);
                await db.SaveChangesAsync(ct);
            }
            return UnauthorizedError("refresh_token_reused", "Refresh token was already used; the session has been revoked.");
        }

        if (!SecureTokens.HashEquals(session.RefreshTokenHash, secretHash) || !session.IsActive(now))
            return InvalidRefreshToken();

        var stillAllowed = await db.Memberships.AnyAsync(m => m.UserId == session.UserId && m.TenantId == session.TenantId, ct)
            && await db.Users.AnyAsync(u => u.Id == session.UserId && u.Status == UserStatus.Active, ct);
        if (!stillAllowed)
        {
            session.Revoke("access_lost", now);
            await db.SaveChangesAsync(ct);
            return InvalidRefreshToken();
        }

        var pair = sessions.Rotate(session);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictError("refresh_conflict", "The session was refreshed concurrently; retry with the latest token.");
        }
        return Ok(pair);
    }

    private ObjectResult EmailTaken() => ConflictError("email_taken", "This e-mail is already registered.");
    private ObjectResult InvalidCredentials() => UnauthorizedError("invalid_credentials", "Invalid e-mail or password.");
    private ObjectResult InvalidRefreshToken() => UnauthorizedError("invalid_refresh_token", "Refresh token is invalid or expired.");
}
