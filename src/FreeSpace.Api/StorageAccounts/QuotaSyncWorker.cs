using FreeSpace.Api.Configurations;
using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.StorageAccounts;

/// <summary>
/// Periodically refreshes quota of active accounts, which also detects revoked credentials early
/// (the account flips to NeedsReauth before an upload ever picks it).
/// </summary>
public sealed class QuotaSyncWorker(IServiceScopeFactory scopes, IOptions<StorageOptions> options, TimeProvider clock, ILogger<QuotaSyncWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundQuotaSync) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                await SyncDueAccountsAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Quota sync round failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncDueAccountsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<StorageAccountService>();

        var dueBefore = clock.GetUtcNow() - TimeSpan.FromMinutes(options.Value.QuotaSyncMinutes);
        // Background work spans all tenants by design.
        var accounts = await db.StorageAccounts.IgnoreQueryFilters([AppDbContext.TenantFilter])
            .Where(a => a.Status == StorageAccountStatus.Active && (a.LastQuotaSyncAt == null || a.LastQuotaSyncAt < dueBefore))
            .OrderBy(a => a.LastQuotaSyncAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var account in accounts)
        {
            await service.SyncQuotaAsync(account, ct);
            await db.SaveChangesAsync(ct); // per account, so one slow provider does not hold the others' results
        }
    }
}
