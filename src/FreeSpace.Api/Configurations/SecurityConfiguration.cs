using FreeSpace.Api.Auth;
using FreeSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FreeSpace.Api.Configurations;

/// <summary>JWT authentication, session validation and the default "authenticated" authorization policy.</summary>
internal static class SecurityConfiguration
{
    public static IServiceCollection AddFreeSpaceSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUser>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddSingleton<TokenService>();
        services.AddScoped<SessionIssuer>();

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
                // Signature checks are not enough: the session and membership must still exist in the database.
                options.Events = new JwtBearerEvents { OnTokenValidated = SessionValidator.ValidateAsync };
            });

        // Everything requires a valid session unless it opts out with [AllowAnonymous].
        // Role checks live in SecureController ([MinimumRole]) so they answer with our ProblemDetails codes.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}
