using FreeSpace.Infrastructure.Security;
using FreeSpace.Infrastructure.Storage.Google;
using FreeSpace.Infrastructure.Storage.S3;

namespace FreeSpace.Api.Configurations;

/// <summary>Binds appsettings/env sections to typed options. Invalid required settings fail at startup.</summary>
internal static class OptionsConfiguration
{
    public static IServiceCollection AddFreeSpaceOptions(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>().BindConfiguration("Jwt").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AuthOptions>().BindConfiguration("Auth").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<RateLimitOptions>().BindConfiguration("RateLimiting").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<EncryptionOptions>().BindConfiguration("Encryption").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<StorageOptions>().BindConfiguration("Storage").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<S3Options>().BindConfiguration("Storage:S3");
        services.AddOptions<GoogleOptions>().BindConfiguration("Google");
        services.AddOptions<DatabaseOptions>().BindConfiguration("Database");
        services.AddOptions<AppOptions>().BindConfiguration("App");
        return services;
    }
}
