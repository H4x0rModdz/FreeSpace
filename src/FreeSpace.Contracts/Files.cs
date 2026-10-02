using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Contracts.Files;

public sealed record CreateFolderRequest(Guid? ParentId, [Required, StringLength(NodeName.MaxLength, MinimumLength = 1)] string Name);

public sealed record RenameNodeRequest([Required, StringLength(NodeName.MaxLength, MinimumLength = 1)] string Name);

/// <param name="ParentId">Destination folder; null moves to the root.</param>
public sealed record MoveNodeRequest(Guid? ParentId);

public sealed record NodeResponse(
    Guid Id, Guid? ParentId, NodeKind Kind, string Name, long SizeBytes, string? MimeType, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ZipRequest([Required, MinLength(1), MaxLength(1000)] Guid[] NodeIds, [StringLength(NodeName.MaxLength)] string? Name);

/// <param name="Url">
/// When <paramref name="Direct"/> is true, an absolute URL at the storage provider (S3 presigned GET).
/// Otherwise a path on this API (<c>/api/v1/content/{token}</c>) that needs no Authorization header.
/// </param>
public sealed record ContentLinkResponse(string Url, bool Direct, DateTimeOffset ExpiresAt);

public sealed record PathSegmentResponse(Guid Id, string Name);

public sealed record NodeDetailsResponse(NodeResponse Node, IReadOnlyList<PathSegmentResponse> Path);

/// <param name="NextCursor">Pass as <c>cursor</c> to get the next page; null on the last page.</param>
public sealed record NodePageResponse(IReadOnlyList<NodeResponse> Items, string? NextCursor);

/// <param name="ItemCount">How many nodes were trashed together with this one (including itself).</param>
public sealed record TrashItemResponse(NodeResponse Node, DateTimeOffset TrashedAt, Guid? TrashedByUserId, int ItemCount);

public sealed record StartUploadRequest(
    Guid? ParentId,
    [Required, StringLength(NodeName.MaxLength, MinimumLength = 1)] string FileName,
    [Range(1, long.MaxValue)] long SizeBytes,
    [StringLength(255)] string? MimeType);

public sealed record PresignChunksRequest([Required, MinLength(1), MaxLength(100)] int[] Chunks);

/// <param name="DirectUploadUrl">
/// Google Drive only: the client may PUT chunks here itself (in order, with Content-Range). Only returned
/// when the upload starts; keep it to resume. S3 clients ask for per-chunk URLs instead.
/// </param>
public sealed record UploadResponse(
    Guid Id, UploadSessionStatus Status, Guid? ParentId, string FileName, long SizeBytes, string MimeType,
    long ChunkSize, int ChunkCount, Guid StorageAccountId, StorageProvider Provider, string? DirectUploadUrl,
    DateTimeOffset ExpiresAt, Guid? NodeId);

public sealed record UploadProgressResponse(UploadResponse Upload, long BytesReceived, IReadOnlyList<int> CompletedChunks);

public sealed record ChunkReceivedResponse(long BytesReceived, IReadOnlyList<int> CompletedChunks);

public sealed record ChunkUrlResponse(int Index, string Url, DateTimeOffset ExpiresAt);

/// <param name="ExpiresAt">Null keeps the link working until revoked.</param>
public sealed record CreateShareRequest(DateTimeOffset? ExpiresAt);

/// <param name="Token">Shown only now; FreeSpace keeps just its hash.</param>
/// <param name="Url">Public page in the web app, when <c>App:FrontendUrl</c> is configured.</param>
public sealed record CreatedShareResponse(Guid Id, Guid NodeId, string Token, string? Url, DateTimeOffset? ExpiresAt);

/// <param name="Reachable">False while the shared item is in the trash (the link answers 404 until it is restored).</param>
public sealed record ShareResponse(Guid Id, Guid NodeId, string NodeName, NodeKind NodeKind, Guid CreatedByUserId, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Reachable);

public sealed record PublicShareResponse(string Name, NodeKind Kind, long SizeBytes, string? MimeType, DateTimeOffset? ExpiresAt);

public sealed record PublicNodeResponse(Guid Id, NodeKind Kind, string Name, long SizeBytes, string? MimeType, DateTimeOffset UpdatedAt);
