using FreeSpace.Domain.Files;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FreeSpace.Infrastructure.Files;

/// <summary>
/// Removes replicas queued for deletion from their providers, then drops objects left without
/// replicas. Works across all tenants (background job), so it bypasses the tenant filter explicitly.
/// </summary>
public sealed class ReplicaPurger(AppDbContext db, StorageProviderRegistry providers, TimeProvider clock, ILogger<ReplicaPurger> logger)
{
    /// <summary>After this many failed attempts a replica is left for an admin to investigate.</summary>
    public const int MaxAttempts = 10;

    /// <returns>Number of replicas removed.</returns>
    public async Task<int> PurgeBatchAsync(int batchSize, CancellationToken ct)
    {
        string[] filter = [AppDbContext.TenantFilter];
        var replicas = await db.Replicas.IgnoreQueryFilters(filter)
            .Where(r => r.Status == ReplicaStatus.Deleting && r.DeleteAttempts < MaxAttempts)
            .OrderBy(r => r.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
        if (replicas.Count == 0) return 0;

        var accountIds = replicas.Select(r => r.StorageAccountId).Distinct().ToList();
        var accounts = await db.StorageAccounts.IgnoreQueryFilters(filter).Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var objectIds = replicas.Select(r => r.ObjectId).Distinct().ToList();
        var sizes = await db.StoredObjects.IgnoreQueryFilters(filter).Where(o => objectIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.SizeBytes, ct);

        var removed = 0;
        foreach (var replica in replicas)
        {
            var account = accounts[replica.StorageAccountId];
            try
            {
                await providers.Get(account.Provider).DeleteObjectAsync(account, replica.ProviderObjectId, ct);
                db.Replicas.Remove(replica);
                account.AdjustUsage(-sizes.GetValueOrDefault(replica.ObjectId), clock.GetUtcNow());
                removed++;
            }
            catch (Exception e) when (e is StorageAuthException or StorageConnectionException)
            {
                logger.LogWarning("Could not delete replica {ReplicaId} from storage account {AccountId}: {Reason}", replica.Id, account.Id, e.Message);
                replica.RecordDeleteFailure(e.Message);
            }
        }
        await db.SaveChangesAsync(ct);

        await db.StoredObjects.IgnoreQueryFilters(filter)
            .Where(o => o.Status == StoredObjectStatus.Deleting && !db.Replicas.IgnoreQueryFilters(filter).Any(r => r.ObjectId == o.Id))
            .ExecuteDeleteAsync(ct);
        return removed;
    }
}
