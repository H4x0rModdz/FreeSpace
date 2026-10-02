using System.Text;
using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Files;

public enum NodeKind
{
    Folder,
    File,
}

/// <summary>
/// An entry in a tenant's virtual tree. Folders exist only here; a file node points to the
/// <see cref="StoredObject"/> holding its bytes, wherever those physically live.
/// </summary>
public sealed class Node : Entity, ITenantOwned
{
    private Node() { }

    private Node(Guid tenantId, Guid? parentId, NodeKind kind, string name, Guid? objectId, long sizeBytes, string? mimeType, Guid createdByUserId, DateTimeOffset now)
    {
        TenantId = tenantId;
        ParentId = parentId;
        Kind = kind;
        SetName(name);
        ObjectId = objectId;
        SizeBytes = sizeBytes;
        MimeType = mimeType;
        CreatedByUserId = createdByUserId;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Node Folder(Guid tenantId, Guid? parentId, string name, Guid userId, DateTimeOffset now) =>
        new(tenantId, parentId, NodeKind.Folder, name, objectId: null, sizeBytes: 0, mimeType: null, userId, now);

    public static Node File(Guid tenantId, Guid? parentId, string name, StoredObject content, Guid userId, DateTimeOffset now) =>
        new(tenantId, parentId, NodeKind.File, name, content.Id, content.SizeBytes, content.MimeType, userId, now);

    public Guid TenantId { get; private set; }
    /// <summary>Null = tenant root.</summary>
    public Guid? ParentId { get; private set; }
    public NodeKind Kind { get; private set; }
    public string Name { get; private set; } = null!;
    /// <summary>Case- and Unicode-normalized name; unique among live siblings.</summary>
    public string NormalizedName { get; private set; } = null!;
    public Guid? ObjectId { get; private set; }
    public long SizeBytes { get; private set; }
    public string? MimeType { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? TrashedAt { get; private set; }
    public Guid? TrashedByUserId { get; private set; }
    /// <summary>The node the user trashed; every node trashed with it shares this id, so they restore together.</summary>
    public Guid? TrashRootId { get; private set; }

    public bool IsFolder => Kind == NodeKind.Folder;
    public bool IsTrashed => TrashedAt is not null;

    public void Rename(string name, DateTimeOffset now)
    {
        SetName(name);
        UpdatedAt = now;
    }

    public void MoveTo(Guid? parentId, DateTimeOffset now)
    {
        ParentId = parentId;
        UpdatedAt = now;
    }

    /// <summary>Un-trashes this node; the caller restores the rest of the batch (same TrashRootId).</summary>
    public void Restore(DateTimeOffset now)
    {
        TrashedAt = null;
        TrashedByUserId = null;
        TrashRootId = null;
        UpdatedAt = now;
    }

    private void SetName(string name)
    {
        if (NodeName.Validate(name) is { } error) throw new ArgumentException(error, nameof(name));
        Name = NodeName.Clean(name);
        NormalizedName = NodeName.Normalize(name);
    }
}

public static class NodeName
{
    public const int MaxLength = 255;

    public static string Clean(string name) => name.Trim().Normalize(NormalizationForm.FormC);

    public static string Normalize(string name) => Clean(name).ToLowerInvariant();

    /// <summary>Returns why a name is unacceptable, or null when it is fine.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        var clean = Clean(name);
        if (clean.Length > MaxLength) return $"Name must be at most {MaxLength} characters.";
        if (clean is "." or "..") return "Name cannot be '.' or '..'.";
        if (clean.Any(c => c is '/' or '\\' || char.IsControl(c))) return "Name cannot contain slashes or control characters.";
        return null;
    }
}
