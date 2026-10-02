using FreeSpace.Api.Common;
using FreeSpace.Domain.Files;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Files;

/// <summary>
/// Serves files through signed, short-lived links (see <c>POST /nodes/{id}/content-link</c>). No session:
/// the signature names the tenant and file, and the file must still exist outside the trash.
/// </summary>
[Route("api/v1/content")]
[Tags("Files")]
[AllowAnonymous]
public sealed class ContentLinksController(AppDbContext db, ContentLinkSigner signer, TimeProvider clock) : BaseController
{
    [HttpGet("{token}"), EnableRateLimiting(RateLimitPolicies.Public)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    public async Task<IActionResult> Get(string token, [FromQuery] bool inline, CancellationToken ct)
    {
        if (!signer.TryVerify(token, clock.GetUtcNow(), out var nodeId, out var tenantId))
            return NotFoundError("link_invalid", "This link is invalid or has expired.");

        var file = await db.Nodes.IgnoreQueryFilters([AppDbContext.TenantFilter])
            .FirstOrDefaultAsync(n => n.Id == nodeId && n.TenantId == tenantId && n.Kind == NodeKind.File && n.TrashedAt == null, ct);
        return file is null
            ? NotFoundError("link_invalid", "This link is invalid or has expired.")
            : new NodeContentResult(file, tenantId, inline);
    }
}
