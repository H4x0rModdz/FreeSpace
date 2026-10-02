using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Contracts.Storage;

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

/// <param name="ReturnUrl">
/// For native apps: where the browser goes after consent, with <c>status</c> and <c>accountId</c> appended.
/// Must be a loopback URL (http://127.0.0.1:port/...) or a registered custom scheme (freespace://...).
/// </param>
public sealed record AuthorizeGoogleRequest([StringLength(2048)] string? ReturnUrl);

public sealed record GoogleAuthorizationResponse(string AuthorizationUrl);

public sealed record RoutingPolicyRequest([Required] UploadRoutingPolicy Policy);

public sealed record RoutingPolicyResponse(UploadRoutingPolicy Policy);

public sealed record StorageAccountResponse(
    Guid Id, StorageProvider Provider, string DisplayName, string? Email, StorageAccountStatus Status, int Priority,
    long? TotalBytes, long UsedBytes, long? AvailableBytes, DateTimeOffset? LastQuotaSyncAt, string? LastError,
    JsonElement? Config, DateTimeOffset CreatedAt);

/// <param name="TotalBytes">Sum of finite quotas; see <paramref name="HasUnlimitedAccount"/>.</param>
public sealed record StorageSummaryResponse(long TotalBytes, long UsedBytes, long AvailableBytes, bool HasUnlimitedAccount, int ActiveAccounts, int AccountsNeedingAttention);
