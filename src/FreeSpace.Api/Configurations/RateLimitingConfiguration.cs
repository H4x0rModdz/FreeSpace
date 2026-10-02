using System.Threading.RateLimiting;
using FreeSpace.Api.Common;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.Configurations;

internal static class RateLimitingConfiguration
{
    public static IServiceCollection AddFreeSpaceRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicies.Auth, http =>
            {
                var permits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value.AuthPermitsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });
        return services;
    }
}
