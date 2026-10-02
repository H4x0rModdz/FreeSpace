using FreeSpace.Domain.Files;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Infrastructure.Files;

public sealed class PathSegment
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int Depth { get; init; }
}

/// <summary>
/// Tree queries the EF model cannot express (recursive subtrees and ancestor chains) plus the
/// multi-row operations built on them. Raw SQL bypasses the global tenant filter, so every query
/// here takes the tenant explicitly.
/// </summary>
public sealed class FileTree(AppDbContext db)
{
    /// <summary>Hard cap on ancestor walks; also stops a corrupted (cyclic) chain from looping.</summary>
    private const int MaxDepth = 256;

    /// <summary>The roots and all their descendants. With <paramref name="liveOnly"/>, already-trashed branches are skipped.</summary>
    public Task<List<Guid>> SubtreeIdsAsync(Guid tenantId, Guid[] rootIds, bool liveOnly, CancellationToken ct) => liveOnly
        ? db.Database.SqlQuery<Guid>($"""
            WITH RECURSIVE subtree(id) AS (
                SELECT id FROM nodes WHERE id = ANY({rootIds}) AND tenant_id = {tenantId} AND trashed_at IS NULL
                UNION ALL
                SELECT n.id FROM nodes n JOIN subtree s ON n.parent_id = s.id
                WHERE n.tenant_id = {tenantId} AND n.trashed_at IS NULL
            )
            SELECT id AS "Value" FROM subtree
            """).ToListAsync(ct)
        : db.Database.SqlQuery<Guid>($"""
            WITH RECURSIVE subtree(id) AS (
                SELECT id FROM nodes WHERE id = ANY({rootIds}) AND tenant_id = {tenantId}
                UNION ALL
                SELECT n.id FROM nodes n JOIN subtree s ON n.parent_id = s.id
                WHERE n.tenant_id = {tenantId}
            )
            SELECT id AS "Value" FROM subtree
            """).ToListAsync(ct);

    /// <summary>The chain from the root down to <paramref name="nodeId"/> (inclusive), root first.</summary>
    public Task<List<PathSegment>> PathAsync(Guid tenantId, Guid nodeId, CancellationToken ct) =>
        db.Database.SqlQuery<PathSegment>($"""
            WITH RECURSIVE chain(id, parent_id, name, depth) AS (
                SELECT id, parent_id, name, 0 FROM nodes WHERE id = {nodeId} AND tenant_id = {tenantId}
                UNION ALL
                SELECT n.id, n.parent_id, n.name, c.depth + 1 FROM nodes n JOIN chain c ON n.id = c.parent_id
                WHERE n.tenant_id = {tenantId} AND c.depth < {MaxDepth}
            )
            SELECT id AS "Id", name AS "Name", depth AS "Depth" FROM chain ORDER BY depth DESC
            """).ToListAsync(ct);

    /// <summary>True when <paramref name="candidateId"/> is <paramref name="nodeId"/> itself or one of its descendants.</summary>
    public async Task<bool> IsSelfOrDescendantAsync(Guid tenantId, Guid nodeId, Guid candidateId, CancellationToken ct) =>
        (await PathAsync(tenantId, candidateId, ct)).Any(p => p.Id == nodeId);

    public Task<bool> NameTakenAsync(Guid? parentId, string name, Guid? excludeNodeId, CancellationToken ct)
    {
        var normalized = NodeName.Normalize(name);
        return db.Nodes.AnyAsync(n => n.ParentId == parentId && n.NormalizedName == normalized && n.TrashedAt == null
                                      && (excludeNodeId == null || n.Id != excludeNodeId), ct);
    }

    /// <summary>"name", or "name (1)", "name (2)"… keeping the extension: "photo (1).jpg".</summary>
    public async Task<string> FreeNameAsync(Guid? parentId, string name, Guid? excludeNodeId, CancellationToken ct)
    {
        if (!await NameTakenAsync(parentId, name, excludeNodeId, ct)) return name;

        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
        if (extension == name) extension = "";
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (candidate.Length > NodeName.MaxLength) candidate = $"{stem[..Math.Max(1, NodeName.MaxLength - extension.Length - 12)]} ({i}){extension}";
            if (!await NameTakenAsync(parentId, candidate, excludeNodeId, ct)) return candidate;
        }
    }

    /// <summary>Serializes structural changes (moves) within a tenant so two concurrent moves cannot form a cycle.</summary>
    public Task LockTreeAsync(Guid tenantId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tenantId.ToString()}, 0))", ct);

    /// <summary>
    /// Permanently removes the subtrees: deletes the nodes, and queues objects no longer referenced
    /// by any node (and their replicas) for removal at the providers. Run inside a transaction.
    /// </summary>
    public async Task<int> DeletePermanentlyAsync(Guid tenantId, Guid[] rootIds, CancellationToken ct)
    {
        var ids = await SubtreeIdsAsync(tenantId, rootIds, liveOnly: false, ct);
        if (ids.Count == 0) return 0;

        var objectIds = await db.Nodes.Where(n => ids.Contains(n.Id) && n.ObjectId != null).Select(n => n.ObjectId!.Value).Distinct().ToListAsync(ct);
        var deleted = await db.Nodes.Where(n => ids.Contains(n.Id)).ExecuteDeleteAsync(ct);

        if (objectIds.Count > 0)
        {
            await db.StoredObjects
                .Where(o => objectIds.Contains(o.Id) && !db.Nodes.Any(n => n.ObjectId == o.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, StoredObjectStatus.Deleting), ct);
            await db.Replicas
                .Where(r => objectIds.Contains(r.ObjectId) && db.StoredObjects.Any(o => o.Id == r.ObjectId && o.Status == StoredObjectStatus.Deleting))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReplicaStatus.Deleting), ct);
        }
        return deleted;
    }
}
