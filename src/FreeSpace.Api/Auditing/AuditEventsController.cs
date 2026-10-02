using System.Text.Json;
using FreeSpace.Api.Common;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Auditing;

[Route("api/v1/tenants/current/audit-events")]
[Tags("Audit")]
[MinimumRole(TenantRole.Admin)]
public sealed class AuditEventsController(AppDbContext db) : SecureController
{
    /// <summary>Newest first. Page with <c>before</c> = the <c>createdAt</c> of the last item received.</summary>
    [HttpGet]
    [ProducesResponseType<List<AuditEventResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int? limit, [FromQuery] DateTimeOffset? before, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.AuditEvents.AsQueryable(); // tenant-scoped by the global filter
        if (before is { } cursor) query = query.Where(e => e.CreatedAt < cursor);

        var rows = await query
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);

        return Ok(rows.Select(e => new AuditEventResponse(
            e.Id, e.ActorUserId, e.Action, e.TargetType, e.TargetId,
            e.DataJson is null ? null : JsonDocument.Parse(e.DataJson).RootElement,
            e.IpAddress, e.CreatedAt)));
    }
}
