using FreeSpace.Api.Common;
using FreeSpace.Domain.Files;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Files;

/// <summary>
/// What anyone holding a share token can see: the shared file, or the shared folder and everything
/// below it. No session; the token is the only credential, and the item must not be in the trash.
/// </summary>
[Route("api/v1/public/shares/{token}")]
[Tags("Public shares")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Public)]
public sealed class PublicSharesController(AppDbContext db, FileTree tree, TimeProvider clock) : BaseController
{
    private const int MaxListing = 1000;
    private static readonly string[] AllTenants = [AppDbContext.TenantFilter];

    [HttpGet]
    [ProducesResponseType<PublicShareResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        if (await ResolveAsync(token, ct) is not var (share, root)) return ShareNotFound();
        return Ok(new PublicShareResponse(root.Name, root.Kind, root.SizeBytes, root.MimeType, share.ExpiresAt));
    }

    /// <summary>Contents of the shared folder, or of a folder inside it (<c>parentId</c>). Folders first.</summary>
    [HttpGet("nodes")]
    [ProducesResponseType<List<PublicNodeResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string token, [FromQuery] Guid? parentId, CancellationToken ct)
    {
        if (await ResolveAsync(token, ct) is not var (share, root)) return ShareNotFound();
        var folder = await FindInShareAsync(share, root, parentId, ct);
        if (folder is not { IsFolder: true }) return NotFoundError("folder_not_found", "Folder not found in this share.");

        var children = await db.Nodes.IgnoreQueryFilters(AllTenants)
            .Where(n => n.TenantId == share.TenantId && n.ParentId == folder.Id && n.TrashedAt == null)
            .OrderBy(n => n.Kind == NodeKind.Folder ? 0 : 1).ThenBy(n => n.NormalizedName)
            .Take(MaxListing)
            .Select(n => new PublicNodeResponse(n.Id, n.Kind, n.Name, n.SizeBytes, n.MimeType, n.UpdatedAt))
            .ToListAsync(ct);
        return Ok(children);
    }

    /// <summary>The shared file, or a file inside the shared folder (<c>nodeId</c>). Honors Range.</summary>
    [HttpGet("content")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    public async Task<IActionResult> Content(string token, [FromQuery] Guid? nodeId, [FromQuery] bool inline, CancellationToken ct)
    {
        if (await ResolveAsync(token, ct) is not var (share, root)) return ShareNotFound();
        var file = await FindInShareAsync(share, root, nodeId, ct);
        return file is { IsFolder: false }
            ? new NodeContentResult(file, share.TenantId, inline)
            : NotFoundError("file_not_found", "File not found in this share.");
    }

    /// <summary>The shared item (or a folder inside it) as a streamed zip.</summary>
    [HttpGet("zip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Zip(string token, [FromQuery] Guid? nodeId, CancellationToken ct)
    {
        if (await ResolveAsync(token, ct) is not var (share, root)) return ShareNotFound();
        var node = await FindInShareAsync(share, root, nodeId, ct);
        return node is null
            ? NotFoundError("node_not_found", "Item not found in this share.")
            : new ZipResult([node], share.TenantId, node.Name + ".zip");
    }

    private async Task<(Share Share, Node Root)?> ResolveAsync(string token, CancellationToken ct)
    {
        var hash = SecureTokens.Hash(token);
        var share = await db.Shares.IgnoreQueryFilters(AllTenants).FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (share is null || !share.IsActive(clock.GetUtcNow())) return null;

        var root = await db.Nodes.IgnoreQueryFilters(AllTenants)
            .FirstOrDefaultAsync(n => n.Id == share.NodeId && n.TenantId == share.TenantId && n.TrashedAt == null, ct);
        return root is null ? null : (share, root);
    }

    /// <summary>The shared root itself, or a live node somewhere below it; anything else is out of reach.</summary>
    private async Task<Node?> FindInShareAsync(Share share, Node root, Guid? nodeId, CancellationToken ct)
    {
        if (nodeId is null || nodeId == root.Id) return root;
        if (!root.IsFolder) return null;

        var node = await db.Nodes.IgnoreQueryFilters(AllTenants)
            .FirstOrDefaultAsync(n => n.Id == nodeId && n.TenantId == share.TenantId && n.TrashedAt == null, ct);
        if (node is null) return null;
        var path = await tree.PathAsync(share.TenantId, node.Id, ct);
        return path.Any(p => p.Id == root.Id) ? node : null;
    }

    private ObjectResult ShareNotFound() => NotFoundError("share_not_found", "This link is invalid, expired or revoked.");
}
