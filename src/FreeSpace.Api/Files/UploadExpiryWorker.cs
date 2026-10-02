using FreeSpace.Api.Configurations;
using FreeSpace.Infrastructure.Files;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.Files;

/// <summary>Cancels uploads nobody finished, so their reserved space returns to the accounts.</summary>
public sealed class UploadExpiryWorker(IServiceScopeFactory scopes, IOptions<StorageOptions> options, TimeProvider clock, ILogger<UploadExpiryWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundUploadExpiry) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        do
        {
            try
            {
                int expired;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    expired = await scope.ServiceProvider.GetRequiredService<UploadService>().ExpireDueAsync(BatchSize, stoppingToken);
                } while (expired == BatchSize);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Upload expiry round failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
