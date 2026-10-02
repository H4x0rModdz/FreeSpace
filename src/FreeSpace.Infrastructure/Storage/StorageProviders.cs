using System.Text.Json;
using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Security;

namespace FreeSpace.Infrastructure.Storage;

/// <summary>The provider rejected the credentials (revoked/expired token, wrong keys). Needs a reconnect.</summary>
public sealed class StorageAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The provider could not be reached or refused the operation for a non-credential reason.</summary>
public sealed class StorageConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What the provider needs to open an upload for one object.</summary>
/// <param name="BrowserOrigin">Origin allowed to send chunks directly (Drive binds CORS to the session).</param>
public sealed record UploadSpec(Guid TenantId, Guid ObjectId, long SizeBytes, string MimeType, string? BrowserOrigin);

/// <summary>An open provider upload: the object key and the provider's handle (session URI, multipart id).</summary>
public sealed record ProviderUpload(string ObjectKey, string Handle, long SizeBytes, long ChunkSize);

/// <param name="DirectUrl">Where a client may send chunks itself, when the provider has a single upload URL (Drive).</param>
public sealed record UploadStart(ProviderUpload Upload, string? DirectUrl);

/// <param name="CompletedChunks">Zero-based indexes of chunks the provider already holds.</param>
public sealed record UploadProgress(long BytesReceived, IReadOnlyList<int> CompletedChunks);

public sealed record PresignedChunk(int Index, string Url, DateTimeOffset ExpiresAt);

/// <param name="ProviderObjectId">Id that addresses the stored bytes from now on (Drive file id, S3 key).</param>
public sealed record CompletedUpload(string ProviderObjectId, long SizeBytes);

/// <summary>Inclusive byte range of a stored object.</summary>
public readonly record struct ByteRange(long From, long To)
{
    public long Length => To - From + 1;
}

/// <summary>The object is gone at the provider (deleted outside FreeSpace).</summary>
public sealed class StorageObjectMissingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A stream that also disposes what keeps it alive (HTTP response, SDK client).</summary>
public sealed class OwnedStream(Stream inner, params IDisposable[] owners) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            foreach (var owner in owners) owner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The upload protocol was violated (out-of-order chunk, wrong size, not supported by the provider).</summary>
public sealed class UploadProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <param name="UsedBytes">Null when the provider has no usage API; FreeSpace then tracks usage itself.</param>
public sealed record QuotaSnapshot(long? TotalBytes, long? UsedBytes);

/// <summary>
/// Operations every storage backend supports. Grows with each phase (uploads, reads, deletes).
/// Implementations throw <see cref="StorageAuthException"/> or <see cref="StorageConnectionException"/>.
/// </summary>
public interface IStorageProvider
{
    StorageProvider Provider { get; }

    /// <summary>Reads quota and, in doing so, proves the credentials still work.</summary>
    Task<QuotaSnapshot> GetQuotaAsync(StorageAccount account, CancellationToken ct);

    /// <summary>Opens an upload session at the provider. Chunks are <see cref="ProviderUpload.ChunkSize"/> bytes (last one shorter).</summary>
    Task<UploadStart> BeginUploadAsync(StorageAccount account, UploadSpec spec, long chunkSize, CancellationToken ct);

    /// <summary>Streams one chunk through the API to the provider (for clients that cannot reach the provider directly).</summary>
    Task<UploadProgress> UploadChunkAsync(StorageAccount account, ProviderUpload upload, int index, long offset, long length, Stream content, CancellationToken ct);

    /// <summary>Short-lived URLs a client can PUT chunks to directly. Providers with a single session URL return none.</summary>
    Task<IReadOnlyList<PresignedChunk>> PresignChunksAsync(StorageAccount account, ProviderUpload upload, IReadOnlyList<int> indexes, CancellationToken ct);

    Task<UploadProgress> GetUploadProgressAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct);

    /// <summary>Finalizes the object; throws <see cref="UploadProtocolException"/> if bytes are missing.</summary>
    Task<CompletedUpload> CompleteUploadAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct);

    /// <summary>Discards an unfinished upload. Best effort and idempotent.</summary>
    Task AbortUploadAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct);

    /// <summary>Streams an object, or one byte range of it. Throws <see cref="StorageObjectMissingException"/> if it is gone.</summary>
    Task<Stream> OpenReadAsync(StorageAccount account, string providerObjectId, ByteRange? range, CancellationToken ct);

    /// <summary>A short-lived URL a client can download from directly, or null when the provider has none (Drive).</summary>
    Task<string?> GetDirectDownloadUrlAsync(StorageAccount account, string providerObjectId, string fileName, TimeSpan lifetime, CancellationToken ct);

    /// <summary>Deletes one stored object. Idempotent: an object that is already gone counts as deleted.</summary>
    Task DeleteObjectAsync(StorageAccount account, string providerObjectId, CancellationToken ct);

    /// <summary>Best-effort cleanup at the provider when an account is removed (e.g. revoke OAuth grant).</summary>
    Task DisconnectAsync(StorageAccount account, CancellationToken ct);
}

public sealed class StorageProviderRegistry(IEnumerable<IStorageProvider> providers)
{
    private readonly Dictionary<StorageProvider, IStorageProvider> _providers = providers.ToDictionary(p => p.Provider);

    public IStorageProvider Get(StorageProvider provider) =>
        _providers.TryGetValue(provider, out var p) ? p : throw new InvalidOperationException($"No provider registered for {provider}.");
}

/// <summary>Typed (de)serialization of an account's encrypted credentials, bound to the account id.</summary>
public static class StorageSecrets
{
    public static void Write<T>(StorageAccount account, T secret, ISecretProtector protector) =>
        account.SetSecret(protector.Protect(JsonSerializer.Serialize(secret, JsonSerializerOptions.Web), account.Id.ToString()));

    public static T Read<T>(StorageAccount account, ISecretProtector protector) =>
        JsonSerializer.Deserialize<T>(protector.Unprotect(account.SecretCiphertext, account.Id.ToString()), JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException("Stored credentials are empty.");

    public static T ReadConfig<T>(StorageAccount account) =>
        JsonSerializer.Deserialize<T>(account.ConfigJson ?? "null", JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException("Storage account configuration is missing.");

    public static string WriteConfig<T>(T config) => JsonSerializer.Serialize(config, JsonSerializerOptions.Web);
}
