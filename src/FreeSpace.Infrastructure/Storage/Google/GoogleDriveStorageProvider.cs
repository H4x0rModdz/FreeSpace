using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace FreeSpace.Infrastructure.Storage.Google;

public sealed record GoogleSecret(string RefreshToken);

public sealed class GoogleDriveStorageProvider(IGoogleApi google, ISecretProtector protector, ILogger<GoogleDriveStorageProvider> logger) : IStorageProvider
{
    public StorageProvider Provider => StorageProvider.GoogleDrive;

    public async Task<QuotaSnapshot> GetQuotaAsync(StorageAccount account, CancellationToken ct)
    {
        var secret = StorageSecrets.Read<GoogleSecret>(account, protector);
        var quota = await google.GetQuotaAsync(secret.RefreshToken, ct);
        return new QuotaSnapshot(quota.LimitBytes, quota.UsageBytes);
    }

    public async Task DisconnectAsync(StorageAccount account, CancellationToken ct)
    {
        try
        {
            await google.RevokeAsync(StorageSecrets.Read<GoogleSecret>(account, protector).RefreshToken, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The grant may already be revoked by the user; removal must not depend on Google.
            logger.LogWarning(e, "Could not revoke Google grant for storage account {AccountId}", account.Id);
        }
    }
}
