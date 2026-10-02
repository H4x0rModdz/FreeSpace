using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using FreeSpace.Infrastructure.Storage;
using FreeSpace.Infrastructure.Storage.Google;
using FreeSpace.Infrastructure.Storage.S3;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.StorageAccounts;

public sealed record ConnectS3Request(
    [Required, StringLength(200, MinimumLength = 1)] string DisplayName,
    [StringLength(512)] string? Endpoint,
    [Required, StringLength(64, MinimumLength = 1)] string Region,
    [Required, StringLength(63, MinimumLength = 3)] string Bucket,
    [StringLength(256)] string? Prefix,
    [Required, StringLength(256, MinimumLength = 1)] string AccessKeyId,
    [Required, StringLength(512, MinimumLength = 1)] string SecretAccessKey,
    bool? ForcePathStyle,
    [Range(1, long.MaxValue)] long? QuotaBytes);

public sealed record UpdateStorageAccountRequest(
    [StringLength(200, MinimumLength = 1)] string? DisplayName,
    [Range(0, 1000)] int? Priority,
    bool? Enabled);

public sealed record GoogleAuthorizationResponse(string AuthorizationUrl);

public sealed record StorageAccountResponse(
    Guid Id, StorageProvider Provider, string DisplayName, string? Email, StorageAccountStatus Status, int Priority,
    long? TotalBytes, long UsedBytes, long? AvailableBytes, DateTimeOffset? LastQuotaSyncAt, string? LastError,
    JsonElement? Config, DateTimeOffset CreatedAt)
{
    // ConfigJson never holds secrets (those live encrypted in SecretCiphertext), so it is safe to expose.
    public static StorageAccountResponse From(StorageAccount a) => new(
        a.Id, a.Provider, a.DisplayName, a.Email, a.Status, a.Priority, a.TotalBytes, a.UsedBytes, a.AvailableBytes,
        a.LastQuotaSyncAt, a.LastError, a.ConfigJson is null ? null : JsonDocument.Parse(a.ConfigJson).RootElement, a.CreatedAt);
}

/// <param name="TotalBytes">Sum of finite quotas; see <paramref name="HasUnlimitedAccount"/>.</param>
public sealed record StorageSummaryResponse(long TotalBytes, long UsedBytes, long AvailableBytes, bool HasUnlimitedAccount, int ActiveAccounts, int AccountsNeedingAttention);

/// <summary>Storage backends of the active tenant. Everyone sees them; only admins change them.</summary>
[Route("api/v1/storage-accounts")]
[Tags("Storage accounts")]
public sealed class StorageAccountsController(AppDbContext db, AuditLog audit, TimeProvider clock) : SecureController
{
    private static readonly TimeSpan OAuthStateLifetime = TimeSpan.FromMinutes(10);

    [HttpGet]
    [ProducesResponseType<List<StorageAccountResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var accounts = await db.StorageAccounts.OrderBy(a => a.Priority).ThenBy(a => a.CreatedAt).ToListAsync(ct);
        return Ok(accounts.Select(StorageAccountResponse.From));
    }

    [HttpGet("summary")]
    [ProducesResponseType<StorageSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var accounts = await db.StorageAccounts.ToListAsync(ct);
        var active = accounts.Where(a => a.Status == StorageAccountStatus.Active).ToList();
        return Ok(new StorageSummaryResponse(
            TotalBytes: active.Sum(a => a.TotalBytes ?? 0),
            UsedBytes: active.Sum(a => a.UsedBytes),
            AvailableBytes: active.Sum(a => a.AvailableBytes ?? 0),
            HasUnlimitedAccount: active.Any(a => a.TotalBytes is null),
            ActiveAccounts: active.Count,
            AccountsNeedingAttention: accounts.Count(a => a.Status == StorageAccountStatus.NeedsReauth)));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<StorageAccountResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await FindAsync(id, ct) is { } account ? Ok(StorageAccountResponse.From(account)) : AccountNotFound();

    [HttpPatch("{id:guid}"), MinimumRole(TenantRole.Admin)]
    [ProducesResponseType<StorageAccountResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, UpdateStorageAccountRequest request, CancellationToken ct)
    {
        var account = await FindAsync(id, ct);
        if (account is null) return AccountNotFound();

        var now = clock.GetUtcNow();
        if (request.DisplayName is { } name) account.Rename(name, now);
        if (request.Priority is { } priority) account.SetPriority(priority, now);
        if (request.Enabled is { } enabled) account.SetEnabled(enabled, now);
        audit.Record(TenantId, UserId, AuditActions.StorageUpdated, "storage_account", account.Id, request);
        await db.SaveChangesAsync(ct);
        return Ok(StorageAccountResponse.From(account));
    }

    /// <summary>Refreshes quota now (also re-validates the credentials).</summary>
    [HttpPost("{id:guid}/sync"), MinimumRole(TenantRole.Admin)]
    [ProducesResponseType<StorageAccountResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Sync(Guid id, [FromServices] StorageAccountService service, CancellationToken ct)
    {
        var account = await FindAsync(id, ct);
        if (account is null) return AccountNotFound();

        await service.SyncQuotaAsync(account, ct);
        await db.SaveChangesAsync(ct);
        return Ok(StorageAccountResponse.From(account));
    }

    [HttpDelete("{id:guid}"), MinimumRole(TenantRole.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid id, [FromServices] StorageProviderRegistry providers, CancellationToken ct)
    {
        var account = await FindAsync(id, ct);
        if (account is null) return AccountNotFound();

        // TODO(phase 3): refuse (or migrate data first) while the account still holds file replicas.
        await providers.Get(account.Provider).DisconnectAsync(account, ct);
        db.StorageAccounts.Remove(account);
        audit.Record(TenantId, UserId, AuditActions.StorageRemoved, "storage_account", account.Id, new { account.Provider, account.DisplayName });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Connects an S3-compatible bucket after proving the keys can write and delete under the prefix.</summary>
    [HttpPost("s3"), MinimumRole(TenantRole.Admin)]
    [ProducesResponseType<StorageAccountResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<StorageAccountResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConnectS3(ConnectS3Request request, [FromServices] S3StorageProvider s3, [FromServices] ISecretProtector protector, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim().TrimEnd('/');
        if (s3.ValidateEndpoint(endpoint) is { } endpointError)
            return BadRequestError("endpoint_not_allowed", endpointError);

        var prefix = string.IsNullOrWhiteSpace(request.Prefix) ? "freespace" : request.Prefix.Trim().Trim('/');
        var config = new S3Config(endpoint, request.Region.Trim(), request.Bucket.Trim(), prefix,
            request.ForcePathStyle ?? endpoint is not null, request.QuotaBytes);
        var secret = new S3Secret(request.AccessKeyId.Trim(), request.SecretAccessKey);

        try
        {
            await s3.VerifyAsync(config, secret, ct);
        }
        catch (Exception e) when (e is StorageAuthException or StorageConnectionException)
        {
            return BadRequestError("s3_connection_failed", e.Message);
        }

        var externalId = $"{(endpoint is null ? $"aws:{config.Region}" : new Uri(endpoint).Authority)}/{config.Bucket}/{config.Prefix}".ToLowerInvariant();
        var configJson = StorageSecrets.WriteConfig(config);
        var now = clock.GetUtcNow();

        var account = await db.StorageAccounts.FirstOrDefaultAsync(a => a.Provider == StorageProvider.S3 && a.ExternalAccountId == externalId, ct);
        var created = account is null;
        if (account is null)
        {
            account = new StorageAccount(TenantId, StorageProvider.S3, externalId, request.DisplayName, email: null, configJson, UserId, now);
            db.StorageAccounts.Add(account);
        }
        else
        {
            account.Reconnect(request.DisplayName, email: null, configJson, now);
        }
        StorageSecrets.Write(account, secret, protector);
        account.RecordQuota(config.QuotaBytes, usedBytes: null, now);

        audit.Record(TenantId, UserId, created ? AuditActions.StorageConnected : AuditActions.StorageReconnected, "storage_account", account.Id,
            new { provider = StorageProvider.S3, endpoint, bucket = config.Bucket, prefix = config.Prefix });
        await db.SaveChangesAsync(ct);

        var response = StorageAccountResponse.From(account);
        return created ? Created($"/api/v1/storage-accounts/{account.Id}", response) : Ok(response);
    }

    /// <summary>Starts the Google consent flow; the browser should be sent to the returned URL.</summary>
    [HttpPost("google/authorize"), MinimumRole(TenantRole.Admin)]
    [ProducesResponseType<GoogleAuthorizationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AuthorizeGoogle([FromServices] IGoogleApi google, CancellationToken ct)
    {
        if (!google.IsConfigured)
            return Error(StatusCodes.Status503ServiceUnavailable, "google_not_configured", "Google OAuth is not configured on this server.");

        var state = SecureTokens.Generate();
        var now = clock.GetUtcNow();
        db.OAuthStates.Add(new OAuthState(StorageProvider.GoogleDrive, SecureTokens.Hash(state), TenantId, UserId, now + OAuthStateLifetime, now));
        await db.SaveChangesAsync(ct);
        return Ok(new GoogleAuthorizationResponse(google.BuildAuthorizationUrl(state)));
    }

    private Task<StorageAccount?> FindAsync(Guid id, CancellationToken ct) => db.StorageAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);

    private ObjectResult AccountNotFound() => NotFoundError("storage_account_not_found", "Storage account not found.");
}
