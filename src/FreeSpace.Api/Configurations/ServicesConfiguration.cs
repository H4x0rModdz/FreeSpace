using FreeSpace.Api.Auditing;
using FreeSpace.Api.Files;
using FreeSpace.Api.StorageAccounts;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Infrastructure.Storage;
using FreeSpace.Infrastructure.Storage.Google;
using FreeSpace.Infrastructure.Storage.S3;

namespace FreeSpace.Api.Configurations;

/// <summary>Application services: auditing, storage providers, the file tree and background workers.</summary>
internal static class ServicesConfiguration
{
    public static IServiceCollection AddFreeSpaceServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuditLog>();

        services.AddSingleton<IGoogleApi, GoogleApi>();
        services.AddSingleton<GoogleDriveStorageProvider>();
        services.AddSingleton<S3StorageProvider>();
        services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<GoogleDriveStorageProvider>());
        services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<S3StorageProvider>());
        services.AddSingleton<StorageProviderRegistry>();
        services.AddScoped<StorageAccountService>();

        services.AddScoped<FileTree>();
        services.AddScoped<ReplicaPurger>();

        services.AddHostedService<QuotaSyncWorker>();
        services.AddHostedService<ReplicaPurgeWorker>();
        return services;
    }
}
