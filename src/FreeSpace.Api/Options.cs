using System.ComponentModel.DataAnnotations;

namespace FreeSpace.Api;

public sealed class JwtOptions
{
    [Required] public string Issuer { get; set; } = "freespace";
    [Required] public string Audience { get; set; } = "freespace";

    /// <summary>HMAC-SHA256 key. Must be at least 32 bytes; never committed.</summary>
    [Required, MinLength(32)] public string SigningKey { get; set; } = "";

    [Range(1, 60)] public int AccessTokenMinutes { get; set; } = 15;
    [Range(1, 365)] public int RefreshTokenDays { get; set; } = 30;
}

public sealed class AuthOptions
{
    public bool AllowRegistration { get; set; } = true;
    [Range(1, 30)] public int InvitationDays { get; set; } = 7;
}

public sealed class RateLimitOptions
{
    /// <summary>Requests per minute per client IP on login/register/refresh/accept.</summary>
    [Range(1, 100_000)] public int AuthPermitsPerMinute { get; set; } = 10;
}

public sealed class ReverseProxyOptions
{
    /// <summary>CIDRs of proxies allowed to set X-Forwarded-For/Proto. Empty = headers ignored.</summary>
    public string[] TrustedNetworks { get; set; } = [];
}

public sealed class DatabaseOptions
{
    public bool MigrateOnStartup { get; set; }
}
