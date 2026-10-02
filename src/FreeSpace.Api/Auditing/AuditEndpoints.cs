using System.Text.Json;
using FreeSpace.Api.Tenants;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Api.Auditing;

public sealed record AuditEventResponse(
    Guid Id, Guid? ActorUserId, string Action, string? TargetType, Guid? TargetId, JsonElement? Data, string? IpAddress, DateTimeOffset CreatedAt);

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/tenants/current/audit-events", List).WithTags("Audit").RequireAuthorization(TenantEndpoints.AdminPolicy);
    }

    /// <summary>Newest first. Page with <c>before</c> = the <c>createdAt</c> of the last item received.</summary>
    private static async Task<IResult> List(AppDbContext db, int? limit, DateTimeOffset? before, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.AuditEvents.AsQueryable(); // tenant-scoped by the global filter
        if (before is { } cursor) query = query.Where(e => e.CreatedAt < cursor);

        var rows = await query
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);

        return TypedResults.Ok(rows.Select(e => new AuditEventResponse(
            e.Id, e.ActorUserId, e.Action, e.TargetType, e.TargetId,
            e.DataJson is null ? null : JsonDocument.Parse(e.DataJson).RootElement,
            e.IpAddress, e.CreatedAt)));
    }
}
