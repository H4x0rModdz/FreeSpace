using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Infrastructure.Files;

/// <summary>
/// Chooses the storage account for a new upload and reserves the space on it. Candidates are active
/// accounts with enough free space (minus other uploads' reservations) that are under the provider's
/// daily upload cap; the tenant's routing policy orders them, and the first successful atomic
/// reservation wins, so concurrent uploads never overcommit an account.
/// </summary>
public sealed class StorageAllocator(AppDbContext db, StorageAccounting accounting, TimeProvider clock)
{
    /// <summary>Google Drive rejects uploads beyond ~750 GB per account per day.</summary>
    public const long GoogleDailyUploadLimit = 750L * 1000 * 1000 * 1000;

    /// <returns>The reserved account (tracked), or null when no account can take the file.</returns>
    public async Task<StorageAccount?> ReserveAsync(Guid tenantId, long sizeBytes, CancellationToken ct)
    {
        var policy = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.UploadRoutingPolicy).FirstAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var candidates = (await db.StorageAccounts.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.Status == StorageAccountStatus.Active)
                .ToListAsync(ct))
            .Where(a => a.FreeBytes is null || a.FreeBytes >= sizeBytes)
            .Where(a => a.Provider != StorageProvider.GoogleDrive || a.UploadedOn(today) + sizeBytes <= GoogleDailyUploadLimit)
            .ToList();
        if (candidates.Count == 0) return null;

        foreach (var candidate in await OrderAsync(candidates, policy, tenantId, ct))
        {
            if (await accounting.TryReserveAsync(candidate.Id, sizeBytes, ct))
                return await db.StorageAccounts.FirstAsync(a => a.Id == candidate.Id, ct);
        }
        return null; // every candidate filled up between the read and the reservation
    }

    private async Task<IEnumerable<StorageAccount>> OrderAsync(List<StorageAccount> candidates, UploadRoutingPolicy policy, Guid tenantId, CancellationToken ct)
    {
        var byPriority = candidates.OrderBy(a => a.Priority).ThenBy(a => a.CreatedAt).ToList();
        switch (policy)
        {
            case UploadRoutingPolicy.Priority:
                return byPriority;
            case UploadRoutingPolicy.RoundRobin:
                var cursor = await NextRoundRobinCursorAsync(tenantId, ct);
                var start = (int)((uint)cursor % (uint)byPriority.Count);
                return byPriority.Skip(start).Concat(byPriority.Take(start));
            default:
                // Accounts without a known limit count as the roomiest.
                return candidates.OrderByDescending(a => a.FreeBytes ?? long.MaxValue).ThenBy(a => a.Priority).ThenBy(a => a.CreatedAt);
        }
    }

    private async Task<int> NextRoundRobinCursorAsync(Guid tenantId, CancellationToken ct) =>
        (await db.Database.SqlQuery<int>(
            $"UPDATE tenants SET round_robin_cursor = round_robin_cursor + 1 WHERE id = {tenantId} RETURNING round_robin_cursor - 1 AS \"Value\"")
            .ToListAsync(ct)).Single();
}
