using System.Security.Claims;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;

namespace FreeSpace.Api.Auth;

public static class FreeSpaceClaims
{
    public const string Subject = "sub";
    public const string Session = "sid";
    public const string Tenant = "tid";
    /// <summary>Added server-side after the session is validated; never read from the token.</summary>
    public const string TenantRole = "trole";
}

/// <summary>
/// The authenticated caller. Only populated after <see cref="SessionValidator"/> accepted the
/// token, so the tenant and role here always reflect the database, not just the JWT.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ITenantContext
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    private bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => GetGuid(FreeSpaceClaims.Subject) ?? throw NotAuthenticated();
    public Guid SessionId => GetGuid(FreeSpaceClaims.Session) ?? throw NotAuthenticated();
    public Guid? TenantId => IsAuthenticated ? GetGuid(FreeSpaceClaims.Tenant) : null;
    public Guid RequiredTenantId => TenantId ?? throw NotAuthenticated();

    public TenantRole Role =>
        Enum.TryParse<TenantRole>(Principal?.FindFirstValue(FreeSpaceClaims.TenantRole), out var role) ? role : throw NotAuthenticated();

    private Guid? GetGuid(string claim) =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(claim), out var id) ? id : null;

    private static InvalidOperationException NotAuthenticated() => new("No authenticated user in the current context.");
}
