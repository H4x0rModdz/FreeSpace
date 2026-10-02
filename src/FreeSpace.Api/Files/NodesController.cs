using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FreeSpace.Api.Files;

public sealed record CreateFolderRequest(Guid? ParentId, [Required, StringLength(NodeName.MaxLength, MinimumLength = 1)] string Name);
public sealed record RenameNodeRequest([Required, StringLength(NodeName.MaxLength, MinimumLength = 1)] string Name);
/// <param name="ParentId">Destination folder; null moves to the root.</param>
public sealed record MoveNodeRequest(Guid? ParentId);

public sealed record NodeResponse(
    Guid Id, Guid? ParentId, NodeKind Kind, string Name, long SizeBytes, string? MimeType, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static NodeResponse From(Node n) => new(n.Id, n.ParentId, n.Kind, n.Name, n.SizeBytes, n.MimeType, n.CreatedAt, n.UpdatedAt);
}

public sealed record PathSegmentResponse(Guid Id, string Name);
public sealed record NodeDetailsResponse(NodeResponse Node, IReadOnlyList<PathSegmentResponse> Path);
/// <param name="NextCursor">Pass as <c>cursor</c> to get the next page; null on the last page.</param>
public sealed record NodePageResponse(IReadOnlyList<NodeResponse> Items, string? NextCursor);

/// <summary>
/// The tenant's virtual tree: folders exist only here, files point to stored objects. Listing is
/// folders first, then by name, with keyset pagination. Viewers read; members and up change things.
/// </summary>
[Route("api/v1/nodes")]
[Tags("Files")]
public sealed class NodesController(AppDbContext db, FileTree tree, AuditLog audit, TimeProvider clock) : SecureController
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    /// <summary>Children of a folder (or of the root when <c>parentId</c> is omitted).</summary>
    [HttpGet]
    [ProducesResponseType<NodePageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] Guid? parentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        if (parentId is { } folderId && await FindLiveFolderAsync(folderId, ct) is null) return FolderNotFound();

        ListCursor? after = null;
        if (cursor is not null && (after = ListCursor.Decode(cursor)) is null)
            return BadRequestError("invalid_cursor", "The cursor is malformed.");

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.Nodes.Where(n => n.ParentId == parentId && n.TrashedAt == null);
        if (after is { IsFolder: true })
            query = query.Where(n => (n.Kind == NodeKind.Folder && string.Compare(n.NormalizedName, after.Name) > 0) || n.Kind == NodeKind.File);
        else if (after is { IsFolder: false })
            query = query.Where(n => n.Kind == NodeKind.File && string.Compare(n.NormalizedName, after.Name) > 0);

        var page = await query
            .OrderBy(n => n.Kind == NodeKind.Folder ? 0 : 1).ThenBy(n => n.NormalizedName)
            .Take(take + 1)
            .ToListAsync(ct);

        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var next = hasMore ? new ListCursor(page[^1].IsFolder, page[^1].NormalizedName).Encode() : null;
        return Ok(new NodePageResponse(page.Select(NodeResponse.From).ToList(), next));
    }

    /// <summary>A node plus its path from the root (breadcrumbs).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<NodeDetailsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.TrashedAt == null, ct);
        if (node is null) return NodeNotFound();

        var path = await tree.PathAsync(TenantId, id, ct);
        return Ok(new NodeDetailsResponse(NodeResponse.From(node), path.Select(p => new PathSegmentResponse(p.Id, p.Name)).ToList()));
    }

    /// <summary>Case-insensitive substring search over names in the whole tree (excluding trash).</summary>
    [HttpGet("search")]
    [ProducesResponseType<List<NodeResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery, Required, StringLength(200, MinimumLength = 1)] string q, [FromQuery] NodeKind? kind, [FromQuery] int? limit, CancellationToken ct)
    {
        var pattern = $"%{EscapeLike(q.Trim())}%";
        var query = db.Nodes.Where(n => n.TrashedAt == null && EF.Functions.ILike(n.Name, pattern, @"\"));
        if (kind is { } k) query = query.Where(n => n.Kind == k);

        var results = await query
            .OrderByDescending(n => n.UpdatedAt)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .ToListAsync(ct);
        return Ok(results.Select(NodeResponse.From));
    }

    [HttpPost("folders"), MinimumRole(TenantRole.Member)]
    [ProducesResponseType<NodeResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateFolder(CreateFolderRequest request, CancellationToken ct)
    {
        if (NodeName.Validate(request.Name) is { } nameError) return BadRequestError("invalid_name", nameError);
        if (request.ParentId is { } parentId && await FindLiveFolderAsync(parentId, ct) is null) return FolderNotFound();
        if (await tree.NameTakenAsync(request.ParentId, request.Name, excludeNodeId: null, ct)) return NameConflict();

        var folder = Node.Folder(TenantId, request.ParentId, request.Name, UserId, clock.GetUtcNow());
        db.Nodes.Add(folder);
        audit.Record(TenantId, UserId, AuditActions.FolderCreated, "node", folder.Id, new { folder.Name, folder.ParentId });
        return await SaveOrConflictAsync(() => Created($"/api/v1/nodes/{folder.Id}", NodeResponse.From(folder)), ct);
    }

    [HttpPatch("{id:guid}"), MinimumRole(TenantRole.Member)]
    [ProducesResponseType<NodeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rename(Guid id, RenameNodeRequest request, CancellationToken ct)
    {
        if (NodeName.Validate(request.Name) is { } nameError) return BadRequestError("invalid_name", nameError);
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.TrashedAt == null, ct);
        if (node is null) return NodeNotFound();
        if (await tree.NameTakenAsync(node.ParentId, request.Name, excludeNodeId: node.Id, ct)) return NameConflict();

        var previous = node.Name;
        node.Rename(request.Name, clock.GetUtcNow());
        audit.Record(TenantId, UserId, AuditActions.NodeRenamed, "node", node.Id, new { previous, current = node.Name });
        return await SaveOrConflictAsync(() => Ok(NodeResponse.From(node)), ct);
    }

    /// <summary>Moves a file or folder (with everything inside) into another folder, or to the root.</summary>
    [HttpPost("{id:guid}/move"), MinimumRole(TenantRole.Member)]
    [ProducesResponseType<NodeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Move(Guid id, MoveNodeRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await tree.LockTreeAsync(TenantId, ct);

        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.TrashedAt == null, ct);
        if (node is null) return NodeNotFound();
        if (node.ParentId == request.ParentId) return Ok(NodeResponse.From(node));

        if (request.ParentId is { } parentId)
        {
            if (await FindLiveFolderAsync(parentId, ct) is null) return FolderNotFound();
            if (node.IsFolder && await tree.IsSelfOrDescendantAsync(TenantId, node.Id, parentId, ct))
                return BadRequestError("invalid_move", "A folder cannot be moved into itself or one of its subfolders.");
        }
        if (await tree.NameTakenAsync(request.ParentId, node.Name, excludeNodeId: node.Id, ct)) return NameConflict();

        var previousParent = node.ParentId;
        node.MoveTo(request.ParentId, clock.GetUtcNow());
        audit.Record(TenantId, UserId, AuditActions.NodeMoved, "node", node.Id, new { from = previousParent, to = request.ParentId });
        var result = await SaveOrConflictAsync(() => Ok(NodeResponse.From(node)), ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>Moves the node and everything inside it to the trash. Restoring brings them all back together.</summary>
    [HttpPost("{id:guid}/trash"), MinimumRole(TenantRole.Member)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Trash(Guid id, CancellationToken ct)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.TrashedAt == null, ct);
        if (node is null) return NodeNotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ids = await tree.SubtreeIdsAsync(TenantId, [id], liveOnly: true, ct);
        var now = clock.GetUtcNow();
        var userId = UserId;
        await db.Nodes.Where(n => ids.Contains(n.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.TrashedAt, now)
            .SetProperty(n => n.TrashedByUserId, userId)
            .SetProperty(n => n.TrashRootId, id)
            .SetProperty(n => n.UpdatedAt, now), ct);

        audit.Record(TenantId, UserId, AuditActions.NodeTrashed, "node", id, new { node.Name, items = ids.Count });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    private Task<Node?> FindLiveFolderAsync(Guid id, CancellationToken ct) =>
        db.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.Kind == NodeKind.Folder && n.TrashedAt == null, ct);

    /// <summary>Saves, turning a lost race on the sibling-name unique index into a 409.</summary>
    private async Task<IActionResult> SaveOrConflictAsync(Func<IActionResult> onSuccess, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return onSuccess();
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return NameConflict();
        }
    }

    private static string EscapeLike(string value) => value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private ObjectResult NodeNotFound() => NotFoundError("node_not_found", "File or folder not found.");
    private ObjectResult FolderNotFound() => NotFoundError("folder_not_found", "Folder not found.");
    private ObjectResult NameConflict() => ConflictError("name_conflict", "An item with this name already exists in the folder.");

    /// <summary>Opaque keyset cursor: position after (kind, normalized name), which is unique among live siblings.</summary>
    private sealed record ListCursor(bool IsFolder, string Name)
    {
        public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

        public static ListCursor? Decode(string value)
        {
            try
            {
                return JsonSerializer.Deserialize<ListCursor>(Base64Url.DecodeFromChars(value));
            }
            catch (Exception e) when (e is FormatException or JsonException)
            {
                return null;
            }
        }
    }
}
