using FreeSpace.Api.Configurations;
using FreeSpace.Infrastructure.Files;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.Files;

/// <summary>Drains replicas queued for deletion (from permanently deleted files) at their providers.</summary>
public sealed class ReplicaPurgeWorker(IServiceScopeFactory scopes, IOptions<StorageOptions> options, TimeProvider clock, ILogger<ReplicaPurgeWorker> logger) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundPurge) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try
            {
                int removed;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    removed = await scope.ServiceProvider.GetRequiredService<ReplicaPurger>().PurgeBatchAsync(BatchSize, stoppingToken);
                } while (removed == BatchSize); // keep going while there is a backlog
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Replica purge round failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
