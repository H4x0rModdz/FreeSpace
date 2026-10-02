using System.ComponentModel.DataAnnotations;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Identity;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

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

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");
        auth.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/refresh", Refresh).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/logout", Logout);
        auth.MapPost("/switch-tenant", SwitchTenant);

        api.MapGet("/me", Me).WithTags("Auth");
    }

    private static async Task<IResult> Register(
        RegisterRequest request, AppDbContext db, IPasswordHasher hasher, SessionIssuer sessions, AuditLog audit,
        IOptions<AuthOptions> authOptions, TimeProvider clock, CancellationToken ct)
    {
        if (!authOptions.Value.AllowRegistration)
            return ApiErrors.Forbidden("registration_disabled", "Self-service registration is disabled.");

        var normalizedEmail = User.NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct))
            return EmailTaken();

        var now = clock.GetUtcNow();
        var user = new User(request.Email, request.Name, hasher.Hash(request.Password), now);
        var tenant = new Tenant(string.IsNullOrWhiteSpace(request.TenantName) ? $"{user.Name}'s space" : request.TenantName, now);
        db.AddRange(user, tenant, new Membership(tenant.Id, user.Id, TenantRole.Owner, now));
        var tokens = sessions.Start(user.Id, tenant.Id);
        audit.Record(tenant.Id, user.Id, AuditActions.UserRegistered, "user", user.Id);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return EmailTaken(); // lost a race with a concurrent registration
        }

        return TypedResults.Created("/api/v1/me", tokens);
    }

    private static IResult EmailTaken() => ApiErrors.Conflict("email_taken", "This e-mail is already registered.");

    private static async Task<IResult> Login(
        LoginRequest request, AppDbContext db, IPasswordHasher hasher, SessionIssuer sessions, AuditLog audit,
        TimeProvider clock, CancellationToken ct)
    {
        var normalizedEmail = User.NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct);
        if (user?.PasswordHash is null)
        {
            hasher.SimulateVerify(request.Password); // same timing whether or not the e-mail exists
            return InvalidCredentials();
        }
        if (!hasher.Verify(user.PasswordHash, request.Password)) return InvalidCredentials();
        if (!user.IsActive) return ApiErrors.Forbidden("account_disabled", "This account is disabled.");

        var memberships = db.Memberships.Where(m => m.UserId == user.Id);
        Guid tenantId;
        if (request.TenantId is { } requested)
        {
            if (!await memberships.AnyAsync(m => m.TenantId == requested, ct))
                return ApiErrors.Forbidden("not_a_member", "You are not a member of this tenant.");
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

        var tokens = sessions.Start(user.Id, tenantId);
        audit.Record(tenantId, user.Id, AuditActions.Login, "user", user.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(tokens);
    }

    private static IResult InvalidCredentials() => ApiErrors.Unauthorized("invalid_credentials", "Invalid e-mail or password.");

    private static async Task<IResult> Refresh(
        RefreshRequest request, AppDbContext db, SessionIssuer sessions, AuditLog audit, TimeProvider clock, CancellationToken ct)
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
            return ApiErrors.Unauthorized("refresh_token_reused", "Refresh token was already used; the session has been revoked.");
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

        var tokens = sessions.Rotate(session);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.Conflict("refresh_conflict", "The session was refreshed concurrently; retry with the latest token.");
        }
        return TypedResults.Ok(tokens);
    }

    private static IResult InvalidRefreshToken() => ApiErrors.Unauthorized("invalid_refresh_token", "Refresh token is invalid or expired.");

    private static async Task<IResult> Logout(AppDbContext db, CurrentUser me, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var session = await db.Sessions.FirstAsync(s => s.Id == me.SessionId, ct);
        session.Revoke("logout", clock.GetUtcNow());
        audit.Record(session.TenantId, me.UserId, AuditActions.Logout, "session", session.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> SwitchTenant(SwitchTenantRequest request, AppDbContext db, CurrentUser me, TokenService tokens, CancellationToken ct)
    {
        if (!await db.Memberships.AnyAsync(m => m.UserId == me.UserId && m.TenantId == request.TenantId, ct))
            return ApiErrors.Forbidden("not_a_member", "You are not a member of this tenant.");

        var session = await db.Sessions.FirstAsync(s => s.Id == me.SessionId, ct);
        session.SwitchTenant(request.TenantId); // invalidates access tokens issued for the previous tenant
        await db.SaveChangesAsync(ct);

        var access = tokens.CreateAccessToken(me.UserId, session.Id, request.TenantId);
        return TypedResults.Ok(new AccessTokenResponse(access.Token, access.ExpiresAt));
    }

    private static async Task<IResult> Me(AppDbContext db, CurrentUser me, CancellationToken ct)
    {
        var user = await db.Users.Where(u => u.Id == me.UserId)
            .Select(u => new MeUser(u.Id, u.Email, u.Name, u.EmailVerifiedAt != null))
            .FirstAsync(ct);
        var role = me.Role;
        var tenant = await db.Tenants.Where(t => t.Id == me.RequiredTenantId)
            .Select(t => new MeTenant(t.Id, t.Name, role))
            .FirstAsync(ct);
        return TypedResults.Ok(new MeResponse(user, tenant));
    }
}
