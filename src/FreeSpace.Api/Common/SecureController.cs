using FreeSpace.Api.Auth;
using FreeSpace.Domain.Common;
using FreeSpace.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FreeSpace.Api.Common;

/// <summary>
/// Minimum role the caller must hold in the active tenant. Applies to a controller or a single action
/// (the action's attribute wins). Enforced by <see cref="SecureController"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class MinimumRoleAttribute(TenantRole role) : Attribute
{
    public TenantRole Role { get; } = role;
}

/// <summary>
/// Base for every authenticated controller. Before any action runs it guarantees:
/// <list type="bullet">
/// <item>a valid session (JWT checked against the database by <see cref="SessionValidator"/>);</item>
/// <item>an active tenant the caller is still a member of;</item>
/// <item>the role required by <see cref="MinimumRoleAttribute"/>, if any.</item>
/// </list>
/// It also exposes the caller (<see cref="UserId"/>, <see cref="TenantId"/>, <see cref="Role"/>) and
/// permission helpers for checks that depend on the target (e.g. "can this admin change that member").
/// </summary>
[Authorize]
public abstract class SecureController : BaseController, IAsyncActionFilter
{
    private CurrentUser? _currentUser;

    /// <summary>The authenticated caller, as validated against the database.</summary>
    protected CurrentUser Caller => _currentUser ??= HttpContext.RequestServices.GetRequiredService<CurrentUser>();

    protected Guid UserId => Caller.UserId;
    protected Guid SessionId => Caller.SessionId;
    /// <summary>Active tenant of the session; every tenant-owned query is already scoped to it.</summary>
    protected Guid TenantId => Caller.RequiredTenantId;
    protected TenantRole Role => Caller.Role;

    protected bool HasRole(TenantRole minimum) => Role >= minimum;

    /// <summary>Defense in depth on top of the global query filter, for entities loaded by other means.</summary>
    protected bool BelongsToTenant(ITenantOwned entity) => entity.TenantId == TenantId;

    protected ObjectResult InsufficientRole(string detail = "Your role does not allow this action.") =>
        ForbiddenError("insufficient_role", detail);

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (Caller.TenantId is null)
        {
            context.Result = UnauthorizedError("no_active_tenant", "The session has no active tenant.");
            return;
        }

        var required = context.ActionDescriptor.EndpointMetadata.OfType<MinimumRoleAttribute>().LastOrDefault();
        if (required is not null && !HasRole(required.Role))
        {
            context.Result = InsufficientRole($"This action requires the '{required.Role.ToString().ToLowerInvariant()}' role or higher.");
            return;
        }

        await next();
    }
}
