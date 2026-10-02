using System.Net;
using System.Net.Http.Headers;
using FreeSpace.Contracts.Files;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Domain.Storage;

namespace FreeSpace.Desktop.Core.Transfers;

/// <param name="Open">Opens a fresh seekable stream over the whole source (called once per chunk attempt).</param>
public sealed record UploadSource(string FileName, long Length, Func<Stream> Open, string? MimeType = null)
{
    public static UploadSource FromFile(string path, string? mimeType = null) =>
        new(Path.GetFileName(path), new FileInfo(path).Length,
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous),
            mimeType ?? MimeTypes.FromFileName(path));
}

public enum UploadRoute
{
    /// <summary>Straight to the provider when possible (Drive session URL, S3 presigned parts); bytes skip the server.</summary>
    Direct,
    /// <summary>Every chunk through the API.</summary>
    ThroughApi,
}

/// <summary>
/// Runs an upload session end to end. Each chunk is retried with backoff; Drive chunks resume from
/// whatever Google already has. If anything fails for good, the session is aborted so the reserved
/// space returns to the account.
/// </summary>
public sealed class UploadEngine(FreeSpaceClient api, HttpClient providerHttp)
{
    private const int MaxAttempts = 4;
    private const int S3Parallelism = 4;
    private const int PresignBatch = 32;

    public async Task<NodeResponse> UploadAsync(UploadSource source, Guid? parentId, IProgress<long>? progress = null,
        UploadRoute route = UploadRoute.Direct, CancellationToken ct = default)
    {
        var upload = await api.StartUploadAsync(new StartUploadRequest(parentId, source.FileName, source.Length, source.MimeType), ct);
        var done = new long[upload.ChunkCount];
        void Report(int index, long bytes)
        {
            Interlocked.Exchange(ref done[index], bytes);
            progress?.Report(done.Sum());
        }

        try
        {
            if (route == UploadRoute.Direct && upload is { Provider: StorageProvider.GoogleDrive, DirectUploadUrl: { } sessionUrl })
            {
                for (var i = 0; i < upload.ChunkCount; i++)
                {
                    var index = i;
                    await RetryAsync(() => PutDriveChunkAsync(new Uri(sessionUrl), upload, source, index, ct), ct);
                    Report(index, ChunkLength(upload, index));
                }
            }
            else if (route == UploadRoute.Direct && upload.Provider == StorageProvider.S3)
            {
                foreach (var batch in Enumerable.Range(0, upload.ChunkCount).Chunk(PresignBatch))
                {
                    var urls = await api.ChunkUrlsAsync(upload.Id, batch, ct);
                    await Parallel.ForEachAsync(urls, new ParallelOptions { MaxDegreeOfParallelism = S3Parallelism, CancellationToken = ct }, async (url, token) =>
                    {
                        await RetryAsync(() => PutPresignedAsync(url.Url, upload, source, url.Index, token), token);
                        Report(url.Index, ChunkLength(upload, url.Index));
                    });
                }
            }
            else
            {
                // Through the API: Drive needs chunks in order, S3 parts can go in parallel.
                var parallelism = upload.Provider == StorageProvider.S3 ? S3Parallelism : 1;
                await Parallel.ForEachAsync(Enumerable.Range(0, upload.ChunkCount), new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (index, token) =>
                {
                    var (offset, length) = (index * upload.ChunkSize, ChunkLength(upload, index));
                    await RetryAsync(() => api.PutChunkAsync(upload.Id, index, () => new SliceStream(source.Open(), offset, length), length, token), token);
                    Report(index, length);
                });
            }

            return await api.CompleteUploadAsync(upload.Id, ct);
        }
        catch
        {
            try
            {
                await api.AbortUploadAsync(upload.Id, CancellationToken.None);
            }
            catch (Exception)
            {
                // The server expires abandoned sessions anyway.
            }
            throw;
        }
    }

    private static long ChunkLength(UploadResponse upload, int index) => Math.Min(upload.ChunkSize, upload.SizeBytes - index * upload.ChunkSize);

    /// <summary>
    /// Sends a Drive chunk to the session URL. After a failed attempt, Google may already hold part of
    /// the chunk, so ask first and send only the rest.
    /// </summary>
    private async Task PutDriveChunkAsync(Uri session, UploadResponse upload, UploadSource source, int index, CancellationToken ct)
    {
        var start = index * upload.ChunkSize;
        var end = start + ChunkLength(upload, index); // exclusive
        var received = await DriveReceivedAsync(session, upload.SizeBytes, ct);
        if (received >= end) return;
        if (received < start)
            throw new InvalidOperationException($"Google Drive is missing bytes before chunk {index}.");

        using var request = new HttpRequestMessage(HttpMethod.Put, session)
        {
            Content = new StreamContent(new SliceStream(source.Open(), received, end - received)),
        };
        request.Content.Headers.ContentLength = end - received;
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(received, end - 1, upload.SizeBytes);
        using var response = await providerHttp.SendAsync(request, ct);
        if ((int)response.StatusCode is not (308 or 200 or 201))
            throw new HttpRequestException($"Google Drive rejected the chunk ({(int)response.StatusCode}).", null, response.StatusCode);
    }

    private async Task<long> DriveReceivedAsync(Uri session, long size, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent([]) };
        request.Content.Headers.ContentRange = ContentRangeHeaderValue.Parse($"bytes */{size}");
        using var response = await providerHttp.SendAsync(request, ct);
        if ((int)response.StatusCode is 200 or 201) return size;
        if ((int)response.StatusCode != 308)
            throw new HttpRequestException($"Google Drive status query failed ({(int)response.StatusCode}).", null, response.StatusCode);
        return response.Headers.TryGetValues("Range", out var values) && values.First() is var range && range.LastIndexOf('-') is var dash and > 0
            ? long.Parse(range[(dash + 1)..]) + 1
            : 0;
    }

    private async Task PutPresignedAsync(string url, UploadResponse upload, UploadSource source, int index, CancellationToken ct)
    {
        var (offset, length) = (index * upload.ChunkSize, ChunkLength(upload, index));
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StreamContent(new SliceStream(source.Open(), offset, length)) };
        request.Content.Headers.ContentLength = length;
        using var response = await providerHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The bucket rejected chunk {index} ({(int)response.StatusCode}).", null, response.StatusCode);
    }

    /// <summary>Retries network failures and server errors with exponential backoff; client errors fail fast.</summary>
    private static async Task RetryAsync(Func<Task> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception e) when (attempt < MaxAttempts && IsTransient(e) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    private static bool IsTransient(Exception e) => e switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status == HttpStatusCode.TooManyRequests,
        ApiException api => (int)api.Status >= 500 || api.Status == HttpStatusCode.TooManyRequests,
        IOException => true,
        _ => false,
    };
}

/// <summary>Downloads into "&lt;file&gt;.part" and resumes from it with a Range request after an interruption.</summary>
public sealed class DownloadEngine(FreeSpaceClient api)
{
    public async Task DownloadAsync(Guid nodeId, long size, string destinationPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var partial = destinationPath + ".part";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > size) offset = 0;

        if (offset < size || size == 0)
        {
            var (content, _) = await api.OpenContentAsync(nodeId, offset, ct);
            await using (content)
            await using (var file = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 20];
                int read;
                var written = offset;
                progress?.Report(written);
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    progress?.Report(written);
                }
            }
        }
        File.Move(partial, destinationPath, overwrite: true);
    }
}

public static class MimeTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".md"] = "text/markdown", [".csv"] = "text/csv", [".json"] = "application/json",
        [".pdf"] = "application/pdf", [".zip"] = "application/zip", [".7z"] = "application/x-7z-compressed",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".mp4"] = "video/mp4", [".mkv"] = "video/x-matroska", [".webm"] = "video/webm", [".mov"] = "video/quicktime",
        [".mp3"] = "audio/mpeg", [".flac"] = "audio/flac", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    public static string FromFileName(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";
}
