using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace FreeSpace.Infrastructure.Storage.Google;

public sealed record GoogleSecret(string RefreshToken);

/// <summary>Non-secret Drive settings, learned on first use.</summary>
public sealed record GoogleConfig(string? AppFolderId);

/// <summary>
/// Google Drive backend. Objects are files named by object id inside the app's "FreeSpace" folder.
/// Uploads use a resumable session: chunks must arrive in order; the session URI itself is the
/// upload credential, so clients can send chunks to it directly.
/// </summary>
public sealed class GoogleDriveStorageProvider(IGoogleApi google, ISecretProtector protector, TimeProvider clock, ILogger<GoogleDriveStorageProvider> logger) : IStorageProvider
{
    public StorageProvider Provider => StorageProvider.GoogleDrive;

    public async Task<QuotaSnapshot> GetQuotaAsync(StorageAccount account, CancellationToken ct)
    {
        var quota = await google.GetQuotaAsync(RefreshToken(account), ct);
        return new QuotaSnapshot(quota.LimitBytes, quota.UsageBytes);
    }

    public async Task<UploadStart> BeginUploadAsync(StorageAccount account, UploadSpec spec, long chunkSize, CancellationToken ct)
    {
        var refreshToken = RefreshToken(account);
        var config = account.ConfigJson is null ? new GoogleConfig(null) : StorageSecrets.ReadConfig<GoogleConfig>(account);
        var folderId = config.AppFolderId;
        if (folderId is null)
        {
            folderId = await google.EnsureAppFolderAsync(refreshToken, ct);
            account.UpdateConfig(StorageSecrets.WriteConfig(config with { AppFolderId = folderId }), clock.GetUtcNow());
        }

        var name = spec.ObjectId.ToString("N");
        var session = await google.StartResumableUploadAsync(refreshToken, name, folderId, spec.MimeType, spec.SizeBytes, spec.BrowserOrigin, ct);
        return new UploadStart(new ProviderUpload(name, session.AbsoluteUri, spec.SizeBytes, chunkSize), DirectUrl: session.AbsoluteUri);
    }

    public async Task<UploadProgress> UploadChunkAsync(StorageAccount account, ProviderUpload upload, int index, long offset, long length, Stream content, CancellationToken ct)
    {
        var session = new Uri(upload.Handle);
        var status = await google.GetResumableStatusAsync(session, upload.SizeBytes, ct);
        if (status.FileId is not null || status.BytesReceived >= offset + length)
            return Progress(upload, status); // retry of a chunk Google already has: idempotent
        if (status.BytesReceived != offset)
            throw new UploadProtocolException("chunk_out_of_order",
                $"Google Drive needs chunks in order; expected chunk {status.BytesReceived / upload.ChunkSize}.");

        return Progress(upload, await google.PutResumableChunkAsync(session, offset, length, upload.SizeBytes, content, ct));
    }

    public Task<IReadOnlyList<PresignedChunk>> PresignChunksAsync(StorageAccount account, ProviderUpload upload, IReadOnlyList<int> indexes, CancellationToken ct) =>
        throw new UploadProtocolException("presign_not_supported", "Google Drive uploads take every chunk at the session URL returned when the upload started.");

    public async Task<UploadProgress> GetUploadProgressAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct) =>
        Progress(upload, await google.GetResumableStatusAsync(new Uri(upload.Handle), upload.SizeBytes, ct));

    public async Task<CompletedUpload> CompleteUploadAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct)
    {
        var status = await google.GetResumableStatusAsync(new Uri(upload.Handle), upload.SizeBytes, ct);
        if (status.FileId is null)
            throw new UploadProtocolException("upload_incomplete", $"Google Drive has {status.BytesReceived} of {upload.SizeBytes} bytes.");
        return new CompletedUpload(status.FileId, status.FileSize ?? upload.SizeBytes);
    }

    public async Task AbortUploadAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct)
    {
        try
        {
            await google.CancelResumableUploadAsync(new Uri(upload.Handle), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogInformation("Cancelling Google upload session failed (it expires on its own): {Reason}", e.Message);
        }
    }

    public Task DeleteObjectAsync(StorageAccount account, string providerObjectId, CancellationToken ct) =>
        google.DeleteFileAsync(RefreshToken(account), providerObjectId, ct);

    public async Task DisconnectAsync(StorageAccount account, CancellationToken ct)
    {
        try
        {
            await google.RevokeAsync(RefreshToken(account), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The grant may already be revoked by the user; removal must not depend on Google.
            logger.LogWarning(e, "Could not revoke Google grant for storage account {AccountId}", account.Id);
        }
    }

    private string RefreshToken(StorageAccount account) => StorageSecrets.Read<GoogleSecret>(account, protector).RefreshToken;

    /// <summary>Drive reports a byte count; chunks below it are complete.</summary>
    private static UploadProgress Progress(ProviderUpload upload, ResumableStatus status)
    {
        var received = status.FileId is not null ? upload.SizeBytes : status.BytesReceived;
        var chunkCount = (int)((upload.SizeBytes + upload.ChunkSize - 1) / upload.ChunkSize);
        var completeChunks = received >= upload.SizeBytes ? chunkCount : (int)(received / upload.ChunkSize);
        return new UploadProgress(received, Enumerable.Range(0, completeChunks).ToList());
    }
}
