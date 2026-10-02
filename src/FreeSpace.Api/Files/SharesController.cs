using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Api.Configurations;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.Files;

/// <summary>Public links to files and folders of the active workspace.</summary>
[Route("api/v1")]
[Tags("Shares")]
public sealed class SharesController(AppDbContext db, AuditLog audit, IOptions<AppOptions> appOptions, TimeProvider clock) : SecureController
{
    [HttpPost("nodes/{nodeId:guid}/shares"), MinimumRole(TenantRole.Member)]
    [ProducesResponseType<CreatedShareResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid nodeId, CreateShareRequest request, CancellationToken ct)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId && n.TrashedAt == null, ct);
        if (node is null) return NotFoundError("node_not_found", "File or folder not found.");
        var now = clock.GetUtcNow();
        if (request.ExpiresAt is { } expiresAt && expiresAt <= now)
            return BadRequestError("invalid_expiry", "The expiry must be in the future.");

        var token = SecureTokens.Generate();
        var share = new Share(TenantId, node.Id, SecureTokens.Hash(token), UserId, request.ExpiresAt, now);
        db.Shares.Add(share);
        audit.Record(TenantId, UserId, AuditActions.ShareCreated, "share", share.Id, new { nodeId = node.Id, node.Name, request.ExpiresAt });
        await db.SaveChangesAsync(ct);

        var url = Uri.TryCreate(appOptions.Value.FrontendUrl, UriKind.Absolute, out var frontend)
            ? $"{frontend.GetLeftPart(UriPartial.Authority)}/s/{token}"
            : null;
        return Created($"/api/v1/shares/{share.Id}", new CreatedShareResponse(share.Id, node.Id, token, url, share.ExpiresAt));
    }

    /// <summary>Links that are neither revoked nor expired.</summary>
    [HttpGet("shares")]
    [ProducesResponseType<List<ShareResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var shares = await (
            from s in db.Shares
            join n in db.Nodes on s.NodeId equals n.Id
            where s.RevokedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now)
            orderby s.CreatedAt descending
            select new ShareResponse(s.Id, n.Id, n.Name, n.Kind, s.CreatedByUserId, s.CreatedAt, s.ExpiresAt, n.TrashedAt == null)
        ).ToListAsync(ct);
        return Ok(shares);
    }

    /// <summary>Stops a link immediately. Its creator or an admin may revoke it.</summary>
    [HttpDelete("shares/{id:guid}"), MinimumRole(TenantRole.Member)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var share = await db.Shares.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (share is null) return NotFoundError("share_not_found", "Share not found.");
        if (share.CreatedByUserId != UserId && !HasRole(TenantRole.Admin))
            return InsufficientRole("Only the creator or an admin can revoke this link.");

        share.Revoke(clock.GetUtcNow());
        audit.Record(TenantId, UserId, AuditActions.ShareRevoked, "share", share.Id, new { share.NodeId });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
