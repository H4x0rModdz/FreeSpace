using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Storage;

public enum StorageProvider
{
    GoogleDrive,
    S3,
}

public enum StorageAccountStatus
{
    /// <summary>Usable for reads and new uploads.</summary>
    Active,
    /// <summary>Credentials were rejected (revoked token, rotated keys); an admin must reconnect.</summary>
    NeedsReauth,
    /// <summary>Turned off by an admin; excluded from new uploads.</summary>
    Disabled,
}

/// <summary>
/// A connected storage backend (one Google Drive account or one S3 bucket/prefix) belonging to a tenant.
/// Non-secret settings live in <see cref="ConfigJson"/>; credentials only in <see cref="SecretCiphertext"/>.
/// </summary>
public sealed class StorageAccount : Entity, ITenantOwned
{
    private StorageAccount() { }

    public StorageAccount(Guid tenantId, StorageProvider provider, string externalAccountId, string displayName, string? email, string? configJson, Guid createdByUserId, DateTimeOffset now)
    {
        TenantId = tenantId;
        Provider = provider;
        ExternalAccountId = externalAccountId;
        DisplayName = displayName.Trim();
        Email = email;
        ConfigJson = configJson;
        CreatedByUserId = createdByUserId;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public StorageProvider Provider { get; private set; }
    /// <summary>Identity at the provider (Google subject id, or endpoint/bucket/prefix for S3); unique per tenant.</summary>
    public string ExternalAccountId { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string? Email { get; private set; }
    public string? ConfigJson { get; private set; }
    /// <summary>Encrypted credentials, bound to this account's id.</summary>
    public string SecretCiphertext { get; private set; } = "";
    public StorageAccountStatus Status { get; private set; } = StorageAccountStatus.Active;
    /// <summary>Lower value = preferred by the priority routing policy.</summary>
    public int Priority { get; private set; }

    /// <summary>Null means the provider reports no limit (or none was configured).</summary>
    public long? TotalBytes { get; private set; }
    public long UsedBytes { get; private set; }
    public DateTimeOffset? LastQuotaSyncAt { get; private set; }
    public string? LastError { get; private set; }

    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public long? AvailableBytes => TotalBytes is { } total ? Math.Max(0, total - UsedBytes) : null;

    public void SetSecret(string ciphertext) => SecretCiphertext = ciphertext;

    /// <summary>Fresh credentials for the same external account (e.g. re-consent after a revoked token).</summary>
    public void Reconnect(string displayName, string? email, string? configJson, DateTimeOffset now)
    {
        DisplayName = displayName.Trim();
        Email = email;
        ConfigJson = configJson;
        Status = StorageAccountStatus.Active;
        LastError = null;
        UpdatedAt = now;
    }

    /// <param name="usedBytes">Null keeps the usage tracked by FreeSpace itself (providers without a usage API).</param>
    public void RecordQuota(long? totalBytes, long? usedBytes, DateTimeOffset now)
    {
        TotalBytes = totalBytes;
        if (usedBytes is { } used) UsedBytes = used;
        LastQuotaSyncAt = now;
        LastError = null;
        UpdatedAt = now;
    }

    public void MarkNeedsReauth(string error, DateTimeOffset now)
    {
        Status = StorageAccountStatus.NeedsReauth;
        LastError = Truncate(error);
        UpdatedAt = now;
    }

    /// <summary>A transient failure (network, provider outage): keep the status, remember the error.</summary>
    public void RecordError(string error, DateTimeOffset now)
    {
        LastError = Truncate(error);
        UpdatedAt = now;
    }

    public void Rename(string displayName, DateTimeOffset now)
    {
        DisplayName = displayName.Trim();
        UpdatedAt = now;
    }

    public void SetPriority(int priority, DateTimeOffset now)
    {
        Priority = priority;
        UpdatedAt = now;
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        if (enabled && Status == StorageAccountStatus.Disabled) Status = StorageAccountStatus.Active;
        else if (!enabled) Status = StorageAccountStatus.Disabled;
        UpdatedAt = now;
    }

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}
