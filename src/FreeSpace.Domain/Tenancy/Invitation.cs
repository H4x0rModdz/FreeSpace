using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Tenancy;

/// <summary>Single-use invite link to join a tenant. Only the token hash is stored.</summary>
public sealed class Invitation : Entity, ITenantOwned
{
    private Invitation() { }

    public Invitation(Guid tenantId, string email, TenantRole role, string tokenHash, Guid invitedByUserId, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        TenantId = tenantId;
        Email = email.Trim();
        Role = role;
        TokenHash = tokenHash;
        InvitedByUserId = invitedByUserId;
        ExpiresAt = expiresAt;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = null!;
    public TenantRole Role { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public Guid InvitedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsPending(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;

    public void Accept(Guid userId, DateTimeOffset now)
    {
        AcceptedAt = now;
        AcceptedByUserId = userId;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
