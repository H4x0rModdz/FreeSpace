using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Infrastructure.Files;

/// <summary>No usable copy of the bytes exists (no replica, or all on unusable accounts).</summary>
public sealed class ContentUnavailableException(string message) : Exception(message);

public sealed record ContentSource(StorageAccount Account, IStorageProvider Provider, string ProviderObjectId);

/// <summary>
/// Finds where a file's bytes can be read from. Prefers replicas on healthy accounts. Takes the tenant
/// explicitly so anonymous paths (signed links, public shares) work without a tenant in context.
/// </summary>
public sealed class ContentReader(AppDbContext db, StorageProviderRegistry providers)
{
    private static readonly string[] AllTenants = [AppDbContext.TenantFilter];

    public async Task<ContentSource> ResolveAsync(Guid tenantId, Node file, CancellationToken ct)
    {
        if (file.ObjectId is not { } objectId) throw new ContentUnavailableException("Folders have no content.");

        var candidates = await (
            from r in db.Replicas.IgnoreQueryFilters(AllTenants)
            join a in db.StorageAccounts.IgnoreQueryFilters(AllTenants) on r.StorageAccountId equals a.Id
            where r.ObjectId == objectId && r.TenantId == tenantId && r.Status == ReplicaStatus.Available
            orderby a.Status == StorageAccountStatus.Active ? 0 : 1, r.CreatedAt
            select new { r.ProviderObjectId, Account = a }
        ).ToListAsync(ct);

        var best = candidates.FirstOrDefault()
                   ?? throw new ContentUnavailableException("No stored copy of this file is available.");
        return new ContentSource(best.Account, providers.Get(best.Account.Provider), best.ProviderObjectId);
    }

    public async Task<Stream> OpenAsync(Guid tenantId, Node file, ByteRange? range, CancellationToken ct)
    {
        var source = await ResolveAsync(tenantId, file, ct);
        return await source.Provider.OpenReadAsync(source.Account, source.ProviderObjectId, range, ct);
    }
}
