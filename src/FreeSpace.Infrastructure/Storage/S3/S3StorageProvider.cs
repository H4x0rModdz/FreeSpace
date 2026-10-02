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

    public async Task DeleteObjectAsync(StorageAccount account, string providerObjectId, CancellationToken ct)
    {
        var config = StorageSecrets.ReadConfig<S3Config>(account);
        using var client = CreateClient(config, StorageSecrets.Read<S3Secret>(account, protector));
        // S3 DeleteObject succeeds for missing keys, which gives us idempotency for free.
        await Run(() => client.DeleteObjectAsync(config.Bucket, providerObjectId, ct));
    }

    public Task DisconnectAsync(StorageAccount account, CancellationToken ct) => Task.CompletedTask; // static keys: nothing to revoke

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

    private static async Task Run(Func<Task> action)
    {
        try
        {
            await action();
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
