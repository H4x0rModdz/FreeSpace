using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Contracts.Auth;

public sealed record RegisterRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required, StringLength(128, MinimumLength = 10)] string Password,
    [StringLength(200, MinimumLength = 1)] string? TenantName);

public sealed record LoginRequest(
    [Required, StringLength(320)] string Email,
    [Required, StringLength(128)] string Password,
    Guid? TenantId);

public sealed record RefreshRequest([Required, StringLength(512)] string RefreshToken);

public sealed record SwitchTenantRequest([Required] Guid TenantId);

public sealed record AccessTokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt)
{
    public string TokenType => "Bearer";
}

public sealed record MeResponse(MeUser User, MeTenant Tenant);

public sealed record MeUser(Guid Id, string Email, string Name, bool EmailVerified);

public sealed record MeTenant(Guid Id, string Name, TenantRole Role);

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt)
{
    public string TokenType => "Bearer";
}
