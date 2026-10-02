using FreeSpace.Domain.Storage;
using Microsoft.Extensions.Logging;

namespace FreeSpace.Infrastructure.Storage;

/// <summary>Provider-independent account maintenance. Mutates the entity; callers save.</summary>
public sealed class StorageAccountService(StorageProviderRegistry providers, TimeProvider clock, ILogger<StorageAccountService> logger)
{
    /// <summary>Refreshes quota. Credential failures flip the account to NeedsReauth; other failures are recorded.</summary>
    public async Task SyncQuotaAsync(StorageAccount account, CancellationToken ct)
    {
        try
        {
            var quota = await providers.Get(account.Provider).GetQuotaAsync(account, ct);
            account.RecordQuota(quota.TotalBytes, quota.UsedBytes, clock.GetUtcNow());
        }
        catch (StorageAuthException e)
        {
            logger.LogWarning("Storage account {AccountId} needs re-authentication: {Reason}", account.Id, e.Message);
            account.MarkNeedsReauth(e.Message, clock.GetUtcNow());
        }
        catch (StorageConnectionException e)
        {
            logger.LogWarning("Quota sync failed for storage account {AccountId}: {Reason}", account.Id, e.Message);
            account.RecordError(e.Message, clock.GetUtcNow());
        }
    }
}
