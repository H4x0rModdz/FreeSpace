using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Files;

public enum UploadSessionStatus
{
    Pending,
    Completed,
    Aborted,
    Expired,
}

/// <summary>
/// An upload in progress: the bytes go to one storage account (reserved up front) in fixed-size
/// chunks, either straight from the client to the provider or through the API. Completing it
/// creates the replica, makes the object available and adds the file node.
/// </summary>
public sealed class UploadSession : Entity, ITenantOwned
{
    private UploadSession() { }

    public UploadSession(Guid tenantId, Guid userId, Guid? parentId, string fileName, string mimeType, long sizeBytes, long chunkSize,
        Guid objectId, Guid storageAccountId, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        TenantId = tenantId;
        UserId = userId;
        ParentId = parentId;
        FileName = NodeName.Clean(fileName);
        MimeType = mimeType;
        SizeBytes = sizeBytes;
        ChunkSize = chunkSize;
        ObjectId = objectId;
        StorageAccountId = storageAccountId;
        ExpiresAt = expiresAt;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    /// <summary>Destination folder; null = root.</summary>
    public Guid? ParentId { get; private set; }
    public string FileName { get; private set; } = null!;
    public string MimeType { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public long ChunkSize { get; private set; }
    public Guid ObjectId { get; private set; }
    public Guid StorageAccountId { get; private set; }
    /// <summary>Name/key of the object at the provider (S3 key, Drive file name).</summary>
    public string ObjectKey { get; private set; } = "";
    /// <summary>Encrypted provider handle (Drive resumable session URI, S3 multipart upload id).</summary>
    public string ProviderStateCiphertext { get; private set; } = "";
    public UploadSessionStatus Status { get; private set; } = UploadSessionStatus.Pending;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public Guid? NodeId { get; private set; }

    public int ChunkCount => (int)((SizeBytes + ChunkSize - 1) / ChunkSize);

    public bool IsPending(DateTimeOffset now) => Status == UploadSessionStatus.Pending && ExpiresAt > now;

    /// <summary>Byte range of a chunk: offset and length (the last chunk may be shorter).</summary>
    public (long Offset, long Length) ChunkRange(int index)
    {
        if (index < 0 || index >= ChunkCount) throw new ArgumentOutOfRangeException(nameof(index));
        var offset = index * ChunkSize;
        return (offset, Math.Min(ChunkSize, SizeBytes - offset));
    }

    public void SetProviderHandle(string objectKey, string providerStateCiphertext)
    {
        ObjectKey = objectKey;
        ProviderStateCiphertext = providerStateCiphertext;
    }

    public void Complete(Guid nodeId, DateTimeOffset now)
    {
        Status = UploadSessionStatus.Completed;
        NodeId = nodeId;
        FinishedAt = now;
    }

    public void End(UploadSessionStatus status, DateTimeOffset now)
    {
        Status = status;
        FinishedAt = now;
    }
}
