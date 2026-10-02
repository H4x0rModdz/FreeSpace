using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Contracts.Tenants;

public sealed record TenantNameRequest([Required, StringLength(200, MinimumLength = 1)] string Name);

public sealed record ChangeRoleRequest([Required] TenantRole Role);

public sealed record TenantSummary(Guid Id, string Name, TenantRole Role, DateTimeOffset JoinedAt);

public sealed record MemberResponse(Guid UserId, string Name, string Email, TenantRole Role, DateTimeOffset JoinedAt);

public sealed record CreateInvitationRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    [Required] TenantRole Role);

public sealed record AcceptInvitationRequest([Required, StringLength(256)] string Token);

public sealed record InvitationResponse(Guid Id, string Email, TenantRole Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>Returned once at creation; the raw token is never stored or shown again.</summary>
public sealed record CreatedInvitationResponse(Guid Id, string Email, TenantRole Role, DateTimeOffset ExpiresAt, string Token);

public sealed record AcceptedInvitationResponse(Guid TenantId, TenantRole Role);
