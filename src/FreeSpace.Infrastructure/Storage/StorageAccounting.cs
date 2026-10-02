using FreeSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Infrastructure.Storage;

/// <summary>
/// Usage and reservation counters of storage accounts, updated with single atomic SQL statements so
/// concurrent uploads and purges never lose updates or overcommit an account. Works across tenants
/// (used by background jobs too), so it addresses accounts by id only.
/// </summary>
public sealed class StorageAccounting(AppDbContext db, TimeProvider clock)
{
    private IQueryable<Domain.Storage.StorageAccount> Accounts(Guid id) =>
        db.StorageAccounts.IgnoreQueryFilters([AppDbContext.TenantFilter]).Where(a => a.Id == id);

    /// <summary>Claims <paramref name="bytes"/> on the account if it still has room. False when it does not.</summary>
    public async Task<bool> TryReserveAsync(Guid accountId, long bytes, CancellationToken ct) =>
        await Accounts(accountId)
            .Where(a => a.TotalBytes == null || a.TotalBytes - a.UsedBytes - a.ReservedBytes >= bytes)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReservedBytes, a => a.ReservedBytes + bytes), ct) == 1;

    public Task ReleaseAsync(Guid accountId, long bytes, CancellationToken ct) =>
        Accounts(accountId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.ReservedBytes, a => a.ReservedBytes - bytes < 0 ? 0 : a.ReservedBytes - bytes), ct);

    /// <summary>Turns a reservation into usage and counts it toward today's upload volume.</summary>
    public Task CommitUploadAsync(Guid accountId, long bytes, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return Accounts(accountId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.UsedBytes, a => a.UsedBytes + bytes)
            .SetProperty(a => a.ReservedBytes, a => a.ReservedBytes - bytes < 0 ? 0 : a.ReservedBytes - bytes)
            .SetProperty(a => a.UploadedTodayBytes, a => a.UploadDay == today ? a.UploadedTodayBytes + bytes : bytes)
            .SetProperty(a => a.UploadDay, today), ct);
    }

    public Task AddUsageAsync(Guid accountId, long deltaBytes, CancellationToken ct) =>
        Accounts(accountId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.UsedBytes, a => a.UsedBytes + deltaBytes < 0 ? 0 : a.UsedBytes + deltaBytes), ct);
}
