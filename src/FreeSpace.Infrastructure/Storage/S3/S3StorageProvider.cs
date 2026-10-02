using System.Globalization;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace FreeSpace.Infrastructure.Storage.S3;

public sealed class S3Options
{
    /// <summary>Allow endpoints on private/loopback networks (self-hosted MinIO on the LAN). Off by default: SSRF.</summary>
    public bool AllowPrivateEndpoints { get; set; }
    /// <summary>Allow plain-HTTP endpoints. Off by default: credentials and data would travel unencrypted.</summary>
    public bool AllowInsecureEndpoints { get; set; }
}

/// <summary>Non-secret settings, stored in clear in <see cref="StorageAccount.ConfigJson"/>.</summary>
public sealed record S3Config(string? Endpoint, string Region, string Bucket, string Prefix, bool ForcePathStyle, long? QuotaBytes);

public sealed record S3Secret(string AccessKeyId, string SecretAccessKey);

public sealed class S3StorageProvider(ISecretProtector protector, IOptions<S3Options> options) : IStorageProvider
{
    public StorageProvider Provider => StorageProvider.S3;

    /// <summary>Returns an error message when the endpoint is not acceptable, null when it is.</summary>
    public string? ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null; // AWS default endpoint for the region

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Endpoint must be an absolute http(s) URL.";
        if (uri.Scheme == Uri.UriSchemeHttp && !options.Value.AllowInsecureEndpoints)
            return "Plain HTTP endpoints are not allowed; use HTTPS.";
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
            return "Endpoint must contain only scheme, host and port.";
        if (IPAddress.TryParse(uri.Host, out var ip) && !options.Value.AllowPrivateEndpoints && !SsrfGuard.IsPublic(ip))
            return "Endpoint points to a non-public address.";
        return null; // hostnames are re-checked against their resolved IP on every connection
    }

    /// <summary>Proves the keys can write and delete under the prefix by round-tripping a probe object.</summary>
    public async Task VerifyAsync(S3Config config, S3Secret secret, CancellationToken ct)
    {
        using var client = CreateClient(config, secret);
        var probeKey = $"{config.Prefix}/.freespace/probe-{Guid.NewGuid():N}";
        await Run(async () =>
        {
            await client.PutObjectAsync(new PutObjectRequest { BucketName = config.Bucket, Key = probeKey, ContentBody = "freespace-probe" }, ct);
            await client.DeleteObjectAsync(config.Bucket, probeKey, ct);
        });
    }

    public async Task<QuotaSnapshot> GetQuotaAsync(StorageAccount account, CancellationToken ct)
    {
        // S3 has no quota/usage API (and listing the bucket is O(objects)), so the quota is the one the
        // admin configured and usage is tracked by FreeSpace. Syncing just re-proves the credentials.
        var config = StorageSecrets.ReadConfig<S3Config>(account);
        await VerifyAsync(config, StorageSecrets.Read<S3Secret>(account, protector), ct);
        return new QuotaSnapshot(config.QuotaBytes, UsedBytes: null);
    }

    public async Task<UploadStart> BeginUploadAsync(StorageAccount account, UploadSpec spec, long chunkSize, CancellationToken ct)
    {
        var (config, client) = Open(account);
        using var _ = client;
        var key = $"{config.Prefix}/{spec.TenantId:N}/{spec.ObjectId:N}";
        var started = await Run(() => client.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest { BucketName = config.Bucket, Key = key, ContentType = spec.MimeType }, ct));
        // Every chunk is one multipart part; clients get per-part presigned URLs instead of a single direct URL.
        return new UploadStart(new ProviderUpload(key, started.UploadId, spec.SizeBytes, chunkSize), DirectUrl: null);
    }

    public async Task<UploadProgress> UploadChunkAsync(StorageAccount account, ProviderUpload upload, int index, long offset, long length, Stream content, CancellationToken ct)
    {
        // Proxy through a presigned URL: the payload is unsigned, so the body streams without buffering.
        var url = (await PresignChunksAsync(account, upload, [index], ct))[0].Url;
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StreamContent(content) };
        request.Content.Headers.ContentLength = length;
        HttpResponseMessage response;
        try
        {
            response = await ProxyClient(options.Value.AllowPrivateEndpoints).SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new StorageConnectionException($"Could not reach the S3 endpoint: {Innermost(e).Message}", e);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new StorageAuthException($"S3 rejected the part upload ({(int)response.StatusCode}).");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UploadProtocolException("upload_expired", "The S3 multipart upload no longer exists; start a new upload.");
            if (!response.IsSuccessStatusCode)
                throw new StorageConnectionException($"S3 part upload failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        }
        return await GetUploadProgressAsync(account, upload, ct);
    }

    public async Task<IReadOnlyList<PresignedChunk>> PresignChunksAsync(StorageAccount account, ProviderUpload upload, IReadOnlyList<int> indexes, CancellationToken ct)
    {
        var (config, client) = Open(account);
        using var _ = client;
        var expiresAt = DateTimeOffset.UtcNow.Add(PresignedUrlLifetime);
        var protocol = config.Endpoint?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true ? Protocol.HTTP : Protocol.HTTPS;

        var result = new List<PresignedChunk>(indexes.Count);
        foreach (var index in indexes)
        {
            var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = config.Bucket,
                Key = upload.ObjectKey,
                Verb = HttpVerb.PUT,
                UploadId = upload.Handle,
                PartNumber = index + 1,
                Expires = expiresAt.UtcDateTime,
                Protocol = protocol,
            });
            result.Add(new PresignedChunk(index, url, expiresAt));
        }
        return result;
    }

    public async Task<UploadProgress> GetUploadProgressAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct)
    {
        var parts = await ListPartsAsync(account, upload, ct);
        return new UploadProgress(parts.Sum(p => p.Size ?? 0), parts.Select(p => (p.PartNumber ?? 0) - 1).Order().ToList());
    }

    public async Task<CompletedUpload> CompleteUploadAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct)
    {
        var parts = await ListPartsAsync(account, upload, ct);
        var chunkCount = (int)((upload.SizeBytes + upload.ChunkSize - 1) / upload.ChunkSize);
        var byNumber = parts.ToDictionary(p => p.PartNumber ?? 0);
        for (var number = 1; number <= chunkCount; number++)
        {
            var expected = number < chunkCount ? upload.ChunkSize : upload.SizeBytes - (chunkCount - 1) * upload.ChunkSize;
            if (!byNumber.TryGetValue(number, out var part) || part.Size != expected)
                throw new UploadProtocolException("upload_incomplete", $"Chunk {number - 1} is missing or has the wrong size.");
        }

        var (config, client) = Open(account);
        using var _ = client;
        await Run(() => client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = config.Bucket,
            Key = upload.ObjectKey,
            UploadId = upload.Handle,
            PartETags = Enumerable.Range(1, chunkCount).Select(n => new PartETag(n, byNumber[n].ETag)).ToList(),
        }, ct));
        return new CompletedUpload(upload.ObjectKey, upload.SizeBytes);
    }

    public async Task AbortUploadAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct)
    {
        var (config, client) = Open(account);
        using var _ = client;
        try
        {
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = config.Bucket, Key = upload.ObjectKey, UploadId = upload.Handle }, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload")
        {
            // Already gone: aborting is idempotent.
        }
    }

    private async Task<List<PartDetail>> ListPartsAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct)
    {
        var (config, client) = Open(account);
        using var _ = client;
        var parts = new List<PartDetail>();
        string? marker = null;
        while (true)
        {
            var page = await Run(() => client.ListPartsAsync(new ListPartsRequest
            {
                BucketName = config.Bucket, Key = upload.ObjectKey, UploadId = upload.Handle, PartNumberMarker = marker,
            }, ct), notFound: () => new UploadProtocolException("upload_expired", "The S3 multipart upload no longer exists; start a new upload."));
            parts.AddRange(page.Parts ?? []);
            if (page.IsTruncated != true) return parts;
            marker = page.NextPartNumberMarker?.ToString(CultureInfo.InvariantCulture);
        }
    }

    public async Task<Stream> OpenReadAsync(StorageAccount account, string providerObjectId, ByteRange? range, CancellationToken ct)
    {
        var (config, client) = Open(account);
        try
        {
            var request = new GetObjectRequest { BucketName = config.Bucket, Key = providerObjectId };
            if (range is { } r) request.ByteRange = new Amazon.S3.Model.ByteRange(r.From, r.To);
            var response = await Run(() => client.GetObjectAsync(request, ct),
                notFound: () => new StorageObjectMissingException("The object no longer exists in the bucket."));
            return new OwnedStream(response.ResponseStream, response, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<string?> GetDirectDownloadUrlAsync(StorageAccount account, string providerObjectId, string fileName, TimeSpan lifetime, CancellationToken ct)
    {
        var (config, client) = Open(account);
        using var _ = client;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = config.Bucket,
            Key = providerObjectId,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = config.Endpoint?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true ? Protocol.HTTP : Protocol.HTTPS,
        };
        // Objects are stored under opaque keys; make the browser save them under the real name.
        request.ResponseHeaderOverrides.ContentDisposition = Files.ContentDispositions.Attachment(fileName);
        return await client.GetPreSignedURLAsync(request);
    }

    public async Task DeleteObjectAsync(StorageAccount account, string providerObjectId, CancellationToken ct)
    {
        var config = StorageSecrets.ReadConfig<S3Config>(account);
        using var client = CreateClient(config, StorageSecrets.Read<S3Secret>(account, protector));
        // S3 DeleteObject succeeds for missing keys, which gives us idempotency for free.
        await Run(() => client.DeleteObjectAsync(config.Bucket, providerObjectId, ct));
    }

    public Task DisconnectAsync(StorageAccount account, CancellationToken ct) => Task.CompletedTask; // static keys: nothing to revoke

    private static readonly TimeSpan PresignedUrlLifetime = TimeSpan.FromHours(1);
    private static readonly Lazy<HttpClient> PublicProxyClient = new(() => CreateProxyClient(allowPrivate: false));
    private static readonly Lazy<HttpClient> PrivateProxyClient = new(() => CreateProxyClient(allowPrivate: true));

    /// <summary>Long-lived clients for streaming proxied parts (sockets are pooled; the SSRF guard still applies).</summary>
    private static HttpClient ProxyClient(bool allowPrivate) => (allowPrivate ? PrivateProxyClient : PublicProxyClient).Value;

    private static HttpClient CreateProxyClient(bool allowPrivate) => new(SsrfGuard.CreateHandler(allowPrivate)) { Timeout = TimeSpan.FromMinutes(30) };

    private (S3Config Config, AmazonS3Client Client) Open(StorageAccount account)
    {
        var config = StorageSecrets.ReadConfig<S3Config>(account);
        return (config, CreateClient(config, StorageSecrets.Read<S3Secret>(account, protector)));
    }

    private AmazonS3Client CreateClient(S3Config config, S3Secret secret)
    {
        var s3Config = new AmazonS3Config
        {
            ForcePathStyle = config.ForcePathStyle,
            // Third-party S3 implementations (R2, B2, MinIO) often reject the SDK's newer default checksums.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            HttpClientFactory = new GuardedHttpClientFactory(options.Value.AllowPrivateEndpoints),
            Timeout = TimeSpan.FromSeconds(30),
            MaxErrorRetry = 2,
        };
        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(config.Region);
        }
        else
        {
            s3Config.ServiceURL = config.Endpoint;
            s3Config.AuthenticationRegion = config.Region;
        }
        return new AmazonS3Client(new BasicAWSCredentials(secret.AccessKeyId, secret.SecretAccessKey), s3Config);
    }

    private static async Task Run(Func<Task> action) => await Run(async () => { await action(); return true; });

    private static async Task<T> Run<T>(Func<Task<T>> action, Func<Exception>? notFound = null)
    {
        try
        {
            return await action();
        }
        catch (AmazonS3Exception e) when (notFound is not null && (e.StatusCode == HttpStatusCode.NotFound || e.ErrorCode == "NoSuchUpload"))
        {
            throw notFound();
        }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                                          || e.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "AccessDenied")
        {
            throw new StorageAuthException($"S3 rejected the credentials ({e.ErrorCode ?? e.StatusCode.ToString()}).", e);
        }
        catch (AmazonS3Exception e)
        {
            throw new StorageConnectionException($"S3 request failed ({e.ErrorCode ?? e.StatusCode.ToString()}): {e.Message}", e);
        }
        catch (Exception e) when (e is AmazonClientException or HttpRequestException)
        {
            throw new StorageConnectionException($"Could not reach the S3 endpoint: {Innermost(e).Message}", e);
        }
    }

    private static Exception Innermost(Exception e) => e.InnerException is null ? e : Innermost(e.InnerException);

    /// <summary>Routes every SDK request through the SSRF-guarded handler.</summary>
    private sealed class GuardedHttpClientFactory(bool allowPrivate) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(SsrfGuard.CreateHandler(allowPrivate));

        public override string GetConfigUniqueString(IClientConfig clientConfig) => $"freespace-guarded:{allowPrivate}";
    }
}
