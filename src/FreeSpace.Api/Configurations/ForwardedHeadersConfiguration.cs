using Microsoft.AspNetCore.HttpOverrides;

namespace FreeSpace.Api.Configurations;

internal static class ForwardedHeadersConfiguration
{
    /// <summary>Honors X-Forwarded-* only from configured proxy networks (e.g. the Docker network).</summary>
    public static IServiceCollection AddFreeSpaceForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var trusted = configuration.GetSection("ReverseProxy").Get<ReverseProxyOptions>()?.TrustedNetworks ?? [];
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var cidr in trusted) options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        });
        return services;
    }
}
