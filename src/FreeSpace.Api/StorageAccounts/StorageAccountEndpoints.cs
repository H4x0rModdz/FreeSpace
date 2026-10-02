using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Common;
using FreeSpace.Api.Tenants;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using FreeSpace.Infrastructure.Storage;
using FreeSpace.Infrastructure.Storage.Google;
using FreeSpace.Infrastructure.Storage.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

public static class StorageAccountEndpoints
{
    private static readonly TimeSpan OAuthStateLifetime = TimeSpan.FromMinutes(10);

    public static void MapStorageAccountEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/storage-accounts").WithTags("Storage accounts");
        group.MapGet("/", List);
        group.MapGet("/summary", Summary);
        group.MapGet("/{id:guid}", Get);
        group.MapPatch("/{id:guid}", Update).RequireAuthorization(TenantEndpoints.AdminPolicy);
        group.MapPost("/{id:guid}/sync", Sync).RequireAuthorization(TenantEndpoints.AdminPolicy);
        group.MapDelete("/{id:guid}", Remove).RequireAuthorization(TenantEndpoints.AdminPolicy);

        group.MapPost("/s3", ConnectS3).RequireAuthorization(TenantEndpoints.AdminPolicy);
        group.MapPost("/google/authorize", AuthorizeGoogle).RequireAuthorization(TenantEndpoints.AdminPolicy);
        // Google redirects the browser here without our bearer token; the OAuth state identifies the tenant.
        group.MapGet("/google/callback", GoogleCallback).AllowAnonymous().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }

    private static async Task<IResult> List(AppDbContext db, CancellationToken ct)
    {
        var accounts = await db.StorageAccounts.OrderBy(a => a.Priority).ThenBy(a => a.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(accounts.Select(StorageAccountResponse.From));
    }

    private static async Task<IResult> Summary(AppDbContext db, CancellationToken ct)
    {
        var accounts = await db.StorageAccounts.ToListAsync(ct);
        var active = accounts.Where(a => a.Status == StorageAccountStatus.Active).ToList();
        return TypedResults.Ok(new StorageSummaryResponse(
            TotalBytes: active.Sum(a => a.TotalBytes ?? 0),
            UsedBytes: active.Sum(a => a.UsedBytes),
            AvailableBytes: active.Sum(a => a.AvailableBytes ?? 0),
            HasUnlimitedAccount: active.Any(a => a.TotalBytes is null),
            ActiveAccounts: active.Count,
            AccountsNeedingAttention: accounts.Count(a => a.Status == StorageAccountStatus.NeedsReauth)));
    }

    private static async Task<IResult> Get(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.StorageAccounts.FirstOrDefaultAsync(a => a.Id == id, ct) is { } account
            ? TypedResults.Ok(StorageAccountResponse.From(account))
            : NotFound();

    private static async Task<IResult> Update(Guid id, UpdateStorageAccountRequest request, AppDbContext db, CurrentUser me, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var account = await db.StorageAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return NotFound();

        var now = clock.GetUtcNow();
        if (request.DisplayName is { } name) account.Rename(name, now);
        if (request.Priority is { } priority) account.SetPriority(priority, now);
        if (request.Enabled is { } enabled) account.SetEnabled(enabled, now);
        audit.Record(account.TenantId, me.UserId, AuditActions.StorageUpdated, "storage_account", account.Id, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(StorageAccountResponse.From(account));
    }

    private static async Task<IResult> Sync(Guid id, AppDbContext db, StorageAccountService service, CancellationToken ct)
    {
        var account = await db.StorageAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return NotFound();

        await service.SyncQuotaAsync(account, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(StorageAccountResponse.From(account));
    }

    private static async Task<IResult> Remove(Guid id, AppDbContext db, StorageProviderRegistry providers, CurrentUser me, AuditLog audit, CancellationToken ct)
    {
        var account = await db.StorageAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return NotFound();

        // TODO(phase 3): refuse (or migrate data first) while the account still holds file replicas.
        await providers.Get(account.Provider).DisconnectAsync(account, ct);
        db.StorageAccounts.Remove(account);
        audit.Record(account.TenantId, me.UserId, AuditActions.StorageRemoved, "storage_account", account.Id, new { account.Provider, account.DisplayName });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ConnectS3(
        ConnectS3Request request, AppDbContext db, S3StorageProvider s3, ISecretProtector protector,
        CurrentUser me, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim().TrimEnd('/');
        if (s3.ValidateEndpoint(endpoint) is { } endpointError)
            return ApiErrors.BadRequest("endpoint_not_allowed", endpointError);

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
            return ApiErrors.BadRequest("s3_connection_failed", e.Message);
        }

        var externalId = $"{(endpoint is null ? $"aws:{config.Region}" : new Uri(endpoint).Authority)}/{config.Bucket}/{config.Prefix}".ToLowerInvariant();
        var configJson = StorageSecrets.WriteConfig(config);
        var now = clock.GetUtcNow();
        var tenantId = me.RequiredTenantId;

        var account = await db.StorageAccounts.FirstOrDefaultAsync(a => a.Provider == StorageProvider.S3 && a.ExternalAccountId == externalId, ct);
        var created = account is null;
        if (account is null)
        {
            account = new StorageAccount(tenantId, StorageProvider.S3, externalId, request.DisplayName, email: null, configJson, me.UserId, now);
            db.StorageAccounts.Add(account);
        }
        else
        {
            account.Reconnect(request.DisplayName, email: null, configJson, now);
        }
        StorageSecrets.Write(account, secret, protector);
        account.RecordQuota(config.QuotaBytes, usedBytes: null, now);

        audit.Record(tenantId, me.UserId, created ? AuditActions.StorageConnected : AuditActions.StorageReconnected, "storage_account", account.Id,
            new { provider = StorageProvider.S3, endpoint, bucket = config.Bucket, prefix = config.Prefix });
        await db.SaveChangesAsync(ct);

        var response = StorageAccountResponse.From(account);
        return created ? TypedResults.Created($"/api/v1/storage-accounts/{account.Id}", response) : TypedResults.Ok(response);
    }

    private static async Task<IResult> AuthorizeGoogle(AppDbContext db, IGoogleApi google, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        if (!google.IsConfigured)
            return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, "google_not_configured", "Google OAuth is not configured on this server.");

        var state = SecureTokens.Generate();
        var now = clock.GetUtcNow();
        db.OAuthStates.Add(new OAuthState(StorageProvider.GoogleDrive, SecureTokens.Hash(state), me.RequiredTenantId, me.UserId, now + OAuthStateLifetime, now));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new GoogleAuthorizationResponse(google.BuildAuthorizationUrl(state)));
    }

    private static async Task<IResult> GoogleCallback(
        string? code, string? state, string? error, AppDbContext db, IGoogleApi google, ISecretProtector protector,
        StorageAccountService service, AuditLog audit, IOptions<AppOptions> appOptions, TimeProvider clock,
        ILogger<StorageAccountService> logger, CancellationToken ct)
    {
        var frontend = appOptions.Value.FrontendUrl;
        if (string.IsNullOrEmpty(state)) return CallbackResult(frontend, "invalid_state");

        var now = clock.GetUtcNow();
        var stateHash = SecureTokens.Hash(state);
        var oauthState = await db.OAuthStates.FirstOrDefaultAsync(s => s.StateHash == stateHash, ct);
        if (oauthState is null || !oauthState.IsUsable(now) || oauthState.Provider != StorageProvider.GoogleDrive)
            return CallbackResult(frontend, "invalid_state");

        oauthState.Consume(now); // single use, even if the rest fails
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return CallbackResult(frontend, error == "access_denied" ? "access_denied" : "authorization_failed");

        // Re-check the initiator is still allowed to manage storage in that tenant.
        var role = await db.Memberships
            .Where(m => m.TenantId == oauthState.TenantId && m.UserId == oauthState.UserId)
            .Select(m => (TenantRole?)m.Role).FirstOrDefaultAsync(ct);
        if (role is null || !TenantPermissions.CanManageMembers(role.Value))
            return CallbackResult(frontend, "forbidden");

        GoogleConnection connection;
        try
        {
            connection = await google.ExchangeCodeAsync(code, ct);
        }
        catch (StorageAuthException e)
        {
            logger.LogInformation("Google connect rejected: {Reason}", e.Message);
            return CallbackResult(frontend, "consent_incomplete");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Google code exchange failed");
            return CallbackResult(frontend, "google_error");
        }

        // Anonymous request: there is no tenant in context, so scope the lookup explicitly.
        var account = await db.StorageAccounts.IgnoreQueryFilters([AppDbContext.TenantFilter])
            .FirstOrDefaultAsync(a => a.TenantId == oauthState.TenantId && a.Provider == StorageProvider.GoogleDrive && a.ExternalAccountId == connection.Subject, ct);
        var created = account is null;
        var displayName = connection.Name ?? connection.Email;
        if (account is null)
        {
            account = new StorageAccount(oauthState.TenantId, StorageProvider.GoogleDrive, connection.Subject, displayName, connection.Email, configJson: null, oauthState.UserId, now);
            db.StorageAccounts.Add(account);
        }
        else
        {
            account.Reconnect(account.DisplayName, connection.Email, account.ConfigJson, now);
        }
        StorageSecrets.Write(account, new GoogleSecret(connection.RefreshToken), protector);
        await service.SyncQuotaAsync(account, ct);

        audit.Record(oauthState.TenantId, oauthState.UserId, created ? AuditActions.StorageConnected : AuditActions.StorageReconnected,
            "storage_account", account.Id, new { provider = StorageProvider.GoogleDrive, email = connection.Email });
        await db.SaveChangesAsync(ct);

        return CallbackResult(frontend, status: null, account.Id);
    }

    /// <summary>Sends the browser back to the frontend; without one configured, answers with JSON.</summary>
    private static IResult CallbackResult(string? frontendUrl, string? status, Guid? accountId = null)
    {
        var outcome = status ?? "connected";
        if (string.IsNullOrEmpty(frontendUrl))
            return status is null
                ? TypedResults.Ok(new { status = outcome, accountId })
                : ApiErrors.BadRequest(outcome, "Google Drive connection failed.");

        var query = $"status={Uri.EscapeDataString(outcome)}" + (accountId is null ? "" : $"&accountId={accountId}");
        return TypedResults.Redirect($"{frontendUrl.TrimEnd('/')}/storage/google/callback?{query}");
    }

    private static IResult NotFound() => ApiErrors.NotFound("storage_account_not_found", "Storage account not found.");
}
