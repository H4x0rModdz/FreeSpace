using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Auditing;

public sealed class AuditEvent : Entity, ITenantOwned
{
    private AuditEvent() { }

    public AuditEvent(Guid tenantId, Guid? actorUserId, string action, string? targetType, Guid? targetId, string? dataJson, string? ipAddress, DateTimeOffset now)
    {
        TenantId = tenantId;
        ActorUserId = actorUserId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        DataJson = dataJson;
        IpAddress = ipAddress;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; } = null!;
    public string? TargetType { get; private set; }
    public Guid? TargetId { get; private set; }
    /// <summary>Free-form JSON payload, stored as jsonb.</summary>
    public string? DataJson { get; private set; }
    public string? IpAddress { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
