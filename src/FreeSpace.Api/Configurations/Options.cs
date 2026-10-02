using System.ComponentModel.DataAnnotations;

namespace FreeSpace.Api.Configurations;

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

    /// <summary>Requests per minute per client IP on anonymous content endpoints (signed links, public shares).</summary>
    [Range(1, 1_000_000)] public int PublicPermitsPerMinute { get; set; } = 300;
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

public sealed class AppOptions
{
    /// <summary>Public URL of the web app; OAuth callbacks redirect the browser back here.</summary>
    public string? FrontendUrl { get; set; }

    /// <summary>
    /// Custom URI schemes registered by native clients (e.g. "freespace" for freespace://...). OAuth flows
    /// may return to these, or to a loopback address (http://127.0.0.1:port), and nowhere else.
    /// </summary>
    public string[] NativeRedirectSchemes { get; set; } = ["freespace"];
}

public sealed class StorageOptions
{
    public bool BackgroundQuotaSync { get; set; } = true;
    /// <summary>Removes bytes of permanently deleted files from the providers.</summary>
    public bool BackgroundPurge { get; set; } = true;
    /// <summary>Cancels abandoned uploads and releases their reserved space.</summary>
    public bool BackgroundUploadExpiry { get; set; } = true;

    /// <summary>Largest single file. 5 TiB is the S3 object limit and above Drive's per-file limit.</summary>
    [Range(1, 5L << 40)] public long MaxUploadBytes { get; set; } = 5L << 40;
    /// <summary>How long an unfinished upload keeps its reservation (Drive sessions live up to a week).</summary>
    [Range(1, 168)] public int UploadSessionHours { get; set; } = 24;

    /// <summary>Limits for streamed zip downloads.</summary>
    [Range(1, 1_000_000)] public int MaxZipEntries { get; set; } = 10_000;
    [Range(1, long.MaxValue)] public long MaxZipBytes { get; set; } = 50L << 30;

    /// <summary>Lifetime of signed download links (also S3 presigned GETs).</summary>
    [Range(1, 1440)] public int ContentLinkMinutes { get; set; } = 60;
    [Range(1, 1440)] public int QuotaSyncMinutes { get; set; } = 15;
}
