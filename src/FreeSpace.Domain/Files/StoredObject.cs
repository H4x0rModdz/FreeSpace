using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Files;

public enum StoredObjectStatus
{
    /// <summary>Upload in progress; not visible through any node yet.</summary>
    Pending,
    Available,
    /// <summary>No node references it anymore; replicas are being removed from providers.</summary>
    Deleting,
}

/// <summary>The logical bytes of a file, independent of where they are stored (see <see cref="Replica"/>).</summary>
public sealed class StoredObject : Entity, ITenantOwned
{
    private StoredObject() { }

    public StoredObject(Guid tenantId, long sizeBytes, string mimeType, StoredObjectStatus status, DateTimeOffset now)
    {
        TenantId = tenantId;
        SizeBytes = sizeBytes;
        MimeType = mimeType;
        Status = status;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public long SizeBytes { get; private set; }
    public string MimeType { get; private set; } = null!;
    /// <summary>Hex SHA-256 of the content, when known.</summary>
    public string? Sha256 { get; private set; }
    public StoredObjectStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void MarkAvailable() => Status = StoredObjectStatus.Available;
}

public enum ReplicaStatus
{
    Pending,
    Available,
    /// <summary>Queued for removal at the provider by the purge worker.</summary>
    Deleting,
}

/// <summary>One physical copy of a <see cref="StoredObject"/> in a storage account.</summary>
public sealed class Replica : Entity, ITenantOwned
{
    private Replica() { }

    public Replica(Guid tenantId, Guid objectId, Guid storageAccountId, string providerObjectId, ReplicaStatus status, DateTimeOffset now)
    {
        TenantId = tenantId;
        ObjectId = objectId;
        StorageAccountId = storageAccountId;
        ProviderObjectId = providerObjectId;
        Status = status;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid ObjectId { get; private set; }
    public Guid StorageAccountId { get; private set; }
    /// <summary>Google Drive file id, or S3 object key.</summary>
    public string ProviderObjectId { get; private set; } = null!;
    public ReplicaStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public int DeleteAttempts { get; private set; }
    public string? LastError { get; private set; }

    public void RecordDeleteFailure(string error)
    {
        DeleteAttempts++;
        LastError = error.Length <= 1000 ? error : error[..1000];
    }
}
