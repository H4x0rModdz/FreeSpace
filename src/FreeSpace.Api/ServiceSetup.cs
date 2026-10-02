using System.Net;
using System.Threading.RateLimiting;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Tenants;
using FreeSpace.Domain.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FreeSpace.Api;

internal static class ServiceSetup
{
    public static IServiceCollection AddFreeSpaceAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, jwt) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = TokenService.CreateSigningKey(jwt.Value),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = FreeSpaceClaims.Subject,
                };
                options.Events = new JwtBearerEvents { OnTokenValidated = SessionValidator.ValidateAsync };
            });

        services.AddAuthorizationBuilder()
            // Everything requires a valid session unless the endpoint opts out with AllowAnonymous.
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(TenantEndpoints.AdminPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx =>
                    Enum.TryParse<TenantRole>(ctx.User.FindFirst(FreeSpaceClaims.TenantRole)?.Value, out var role)
                    && TenantPermissions.CanManageMembers(role)));

        return services;
    }

    public static IServiceCollection AddFreeSpaceRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthEndpoints.RateLimitPolicy, http =>
            {
                var permits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value.AuthPermitsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });
        return services;
    }

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
