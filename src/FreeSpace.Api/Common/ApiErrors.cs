using Microsoft.AspNetCore.WebUtilities;

namespace FreeSpace.Api.Common;

/// <summary>ProblemDetails responses carrying a stable machine-readable <c>code</c>.</summary>
public static class ApiErrors
{
    public static IResult Problem(int status, string code, string detail) =>
        TypedResults.Problem(
            statusCode: status,
            title: ReasonPhrases.GetReasonPhrase(status),
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult BadRequest(string code, string detail) => Problem(StatusCodes.Status400BadRequest, code, detail);
    public static IResult Unauthorized(string code, string detail) => Problem(StatusCodes.Status401Unauthorized, code, detail);
    public static IResult Forbidden(string code, string detail) => Problem(StatusCodes.Status403Forbidden, code, detail);
    public static IResult NotFound(string code, string detail) => Problem(StatusCodes.Status404NotFound, code, detail);
    public static IResult Conflict(string code, string detail) => Problem(StatusCodes.Status409Conflict, code, detail);
}
