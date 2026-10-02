using System.Text.Json;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Auditing;
using FreeSpace.Infrastructure.Persistence;

namespace FreeSpace.Api.Auditing;

public static class AuditActions
{
    public const string UserRegistered = "user.registered";
    public const string Login = "auth.login";
    public const string Logout = "auth.logout";
    public const string RefreshTokenReused = "auth.refresh_token_reused";
    public const string TenantCreated = "tenant.created";
    public const string TenantRenamed = "tenant.renamed";
    public const string MemberRoleChanged = "member.role_changed";
    public const string MemberRemoved = "member.removed";
    public const string InvitationCreated = "invitation.created";
    public const string InvitationRevoked = "invitation.revoked";
    public const string InvitationAccepted = "invitation.accepted";
}

/// <summary>
/// Stages audit events on the DbContext so they commit atomically with the action they describe.
/// </summary>
public sealed class AuditLog(AppDbContext db, IHttpContextAccessor accessor, TimeProvider clock)
{
    public void Record(Guid tenantId, Guid? actorUserId, string action, string? targetType = null, Guid? targetId = null, object? data = null)
    {
        var json = data is null ? null : JsonSerializer.Serialize(data, JsonSerializerOptions.Web);
        db.AuditEvents.Add(new AuditEvent(tenantId, actorUserId, action, targetType, targetId, json, accessor.HttpContext?.IpAddress(), clock.GetUtcNow()));
    }
}
