using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Files;

/// <summary>
/// Trash entries are the nodes a user trashed directly; their contents travel with them.
/// Permanent deletion queues the bytes for removal at the providers.
/// </summary>
[Route("api/v1/trash")]
[Tags("Files")]
public sealed class TrashController(AppDbContext db, FileTree tree, AuditLog audit, TimeProvider clock) : SecureController
{
    [HttpGet]
    [ProducesResponseType<List<TrashItemResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var roots = await db.Nodes.Where(n => n.TrashRootId == n.Id).OrderByDescending(n => n.TrashedAt).ToListAsync(ct);
        var rootIds = roots.Select(r => r.Id).ToList();
        var counts = await db.Nodes.Where(n => n.TrashRootId != null && rootIds.Contains(n.TrashRootId.Value))
            .GroupBy(n => n.TrashRootId!.Value)
            .Select(g => new { RootId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RootId, x => x.Count, ct);

        return Ok(roots.Select(r => new TrashItemResponse(ResponseMappings.ToNodeResponse(r), r.TrashedAt!.Value, r.TrashedByUserId, counts.GetValueOrDefault(r.Id, 1))));
    }

    /// <summary>
    /// Restores an entry and its contents to the original folder, or to the root if that folder is gone
    /// or itself in the trash. A name clash at the destination gets a " (n)" suffix.
    /// </summary>
    [HttpPost("{id:guid}/restore"), MinimumRole(TenantRole.Member)]
    [ProducesResponseType<NodeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await tree.LockTreeAsync(TenantId, ct);

        var root = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.TrashRootId == id, ct);
        if (root is null) return TrashItemNotFound();

        var now = clock.GetUtcNow();
        var parentAlive = root.ParentId is { } parentId
            && await db.Nodes.AnyAsync(n => n.Id == parentId && n.TrashedAt == null, ct);
        var destination = parentAlive ? root.ParentId : null;
        var name = await tree.FreeNameAsync(destination, root.Name, excludeNodeId: root.Id, ct);

        await db.Nodes.Where(n => n.TrashRootId == id && n.Id != id).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.TrashedAt, (DateTimeOffset?)null)
            .SetProperty(n => n.TrashedByUserId, (Guid?)null)
            .SetProperty(n => n.TrashRootId, (Guid?)null)
            .SetProperty(n => n.UpdatedAt, now), ct);
        root.MoveTo(destination, now);
        if (name != root.Name) root.Rename(name, now);
        root.Restore(now);

        audit.Record(TenantId, UserId, AuditActions.NodeRestored, "node", root.Id, new { root.Name, parentId = destination });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Ok(ResponseMappings.ToNodeResponse(root));
    }

    /// <summary>Deletes an entry forever. Its files' bytes are removed from the providers in the background.</summary>
    [HttpDelete("{id:guid}"), MinimumRole(TenantRole.Member)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var root = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.TrashRootId == id, ct);
        if (root is null) return TrashItemNotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = await tree.DeletePermanentlyAsync(TenantId, [id], ct);
        audit.Record(TenantId, UserId, AuditActions.NodeDeleted, "node", id, new { root.Name, items = deleted });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    /// <summary>Deletes every trash entry of the tenant forever.</summary>
    [HttpDelete, MinimumRole(TenantRole.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> EmptyTrash(CancellationToken ct)
    {
        var rootIds = await db.Nodes.Where(n => n.TrashRootId == n.Id).Select(n => n.Id).ToArrayAsync(ct);
        if (rootIds.Length == 0) return NoContent();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = await tree.DeletePermanentlyAsync(TenantId, rootIds, ct);
        audit.Record(TenantId, UserId, AuditActions.TrashEmptied, "tenant", TenantId, new { entries = rootIds.Length, items = deleted });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    private ObjectResult TrashItemNotFound() => NotFoundError("trash_item_not_found", "Trash entry not found.");
}
