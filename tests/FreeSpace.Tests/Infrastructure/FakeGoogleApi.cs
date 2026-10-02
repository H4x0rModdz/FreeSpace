using System.Collections.Concurrent;
using FreeSpace.Infrastructure.Storage;
using FreeSpace.Infrastructure.Storage.Google;

namespace FreeSpace.Tests.Infrastructure;

/// <summary>In-memory stand-in for Google OAuth, Drive quota and Drive resumable uploads.</summary>
public sealed class FakeGoogleApi : IGoogleApi
{
    private readonly ConcurrentDictionary<string, GoogleConnection> _codes = new();
    private readonly ConcurrentDictionary<Uri, FakeUpload> _uploads = new();

    private sealed class FakeUpload(string name, string folderId, long size)
    {
        public string Name { get; } = name;
        public string FolderId { get; } = folderId;
        public long Size { get; } = size;
        public MemoryStream Data { get; } = new();
        public string? FileId { get; set; }
    }

    /// <summary>Completed uploads: Drive file id → (name, bytes).</summary>
    public ConcurrentDictionary<string, (string Name, byte[] Content)> Files { get; } = new();
    public ConcurrentDictionary<Uri, bool> CancelledUploads { get; } = new();
    public string? LastUploadOrigin { get; private set; }

    public GoogleQuota Quota { get; set; } = new(LimitBytes: 15L << 30, UsageBytes: 1L << 30);
    public ConcurrentDictionary<string, bool> RevokedTokens { get; } = new();
    public ConcurrentDictionary<string, bool> DeletedFiles { get; } = new();
    public string? LastState { get; private set; }

    public bool IsConfigured => true;

    /// <summary>Simulates the user consenting at Google: returns the code Google would send to the callback.</summary>
    public string Consent(string subject, string email, string? refreshToken = null)
    {
        var code = $"code-{Guid.NewGuid():N}";
        _codes[code] = new GoogleConnection(subject, email, "Drive " + email, refreshToken ?? $"refresh-{Guid.NewGuid():N}");
        return code;
    }

    public string BuildAuthorizationUrl(string state)
    {
        LastState = state;
        return $"https://accounts.google.test/o/oauth2/auth?state={Uri.EscapeDataString(state)}";
    }

    public Task<GoogleConnection> ExchangeCodeAsync(string code, CancellationToken ct) =>
        _codes.TryRemove(code, out var connection)
            ? Task.FromResult(connection)
            : throw new StorageAuthException("Unknown authorization code.");

    public Task<GoogleQuota> GetQuotaAsync(string refreshToken, CancellationToken ct) =>
        RevokedTokens.ContainsKey(refreshToken)
            ? throw new StorageAuthException("invalid_grant")
            : Task.FromResult(Quota);

    public Task DeleteFileAsync(string refreshToken, string fileId, CancellationToken ct)
    {
        if (RevokedTokens.ContainsKey(refreshToken)) throw new StorageAuthException("invalid_grant");
        DeletedFiles[fileId] = true;
        return Task.CompletedTask;
    }

    public Task<string> EnsureAppFolderAsync(string refreshToken, CancellationToken ct) =>
        RevokedTokens.ContainsKey(refreshToken) ? throw new StorageAuthException("invalid_grant") : Task.FromResult("fake-app-folder");

    public Task<Uri> StartResumableUploadAsync(string refreshToken, string name, string folderId, string mimeType, long size, string? origin, CancellationToken ct)
    {
        if (RevokedTokens.ContainsKey(refreshToken)) throw new StorageAuthException("invalid_grant");
        LastUploadOrigin = origin;
        var session = new Uri($"https://upload.google.test/session/{Guid.NewGuid():N}");
        _uploads[session] = new FakeUpload(name, folderId, size);
        return Task.FromResult(session);
    }

    public Task<ResumableStatus> GetResumableStatusAsync(Uri session, long size, CancellationToken ct) =>
        Task.FromResult(Status(Upload(session)));

    public async Task<ResumableStatus> PutResumableChunkAsync(Uri session, long offset, long length, long size, Stream content, CancellationToken ct)
    {
        var upload = Upload(session);
        if (offset != upload.Data.Length) return Status(upload); // like Drive: it reports what it actually has
        var buffer = new byte[length];
        await content.ReadExactlyAsync(buffer, ct);
        upload.Data.Write(buffer);
        if (upload.Data.Length == upload.Size)
        {
            upload.FileId = $"drive-file-{Guid.NewGuid():N}";
            Files[upload.FileId] = (upload.Name, upload.Data.ToArray());
        }
        return Status(upload);
    }

    public Task CancelResumableUploadAsync(Uri session, CancellationToken ct)
    {
        _uploads.TryRemove(session, out _);
        CancelledUploads[session] = true;
        return Task.CompletedTask;
    }

    private FakeUpload Upload(Uri session) =>
        _uploads.TryGetValue(session, out var upload) ? upload : throw new UploadProtocolException("upload_expired", "Unknown session.");

    private static ResumableStatus Status(FakeUpload upload) =>
        new(upload.Data.Length, upload.FileId, upload.FileId is null ? null : upload.Size);

    public Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        RevokedTokens[refreshToken] = true;
        return Task.CompletedTask;
    }
}
