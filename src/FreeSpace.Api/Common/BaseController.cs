using Microsoft.AspNetCore.Mvc;

namespace FreeSpace.Api.Common;

public static class RateLimitPolicies
{
    /// <summary>Per-IP limit on credential-handling endpoints.</summary>
    public const string Auth = "auth";
    /// <summary>Per-IP limit on anonymous content endpoints (signed links, public shares).</summary>
    public const string Public = "public";
}

/// <summary>
/// Root of every controller. Owns the error contract: ProblemDetails (RFC 9457) with a stable
/// machine-readable <c>code</c>. Controllers that are reachable without a session (login, OAuth
/// callbacks) derive from this directly; everything else derives from <see cref="SecureController"/>.
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class BaseController : ControllerBase
{
    protected ObjectResult Error(int status, string code, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    protected ObjectResult BadRequestError(string code, string detail) => Error(StatusCodes.Status400BadRequest, code, detail);
    protected ObjectResult UnauthorizedError(string code, string detail) => Error(StatusCodes.Status401Unauthorized, code, detail);
    protected ObjectResult ForbiddenError(string code, string detail) => Error(StatusCodes.Status403Forbidden, code, detail);
    protected ObjectResult NotFoundError(string code, string detail) => Error(StatusCodes.Status404NotFound, code, detail);
    protected ObjectResult ConflictError(string code, string detail) => Error(StatusCodes.Status409Conflict, code, detail);
}
