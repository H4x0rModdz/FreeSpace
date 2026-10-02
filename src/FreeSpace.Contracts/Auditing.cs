using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Contracts.Auditing;

public sealed record AuditEventResponse(
    Guid Id, Guid? ActorUserId, string Action, string? TargetType, Guid? TargetId, JsonElement? Data, string? IpAddress, DateTimeOffset CreatedAt);
