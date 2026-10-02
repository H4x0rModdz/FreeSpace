using System.Net;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Tenants;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Tests.Infrastructure;

namespace FreeSpace.Tests;

[Collection(ApiCollection.Name)]
public sealed class TenancyTests(ApiFactory factory)
{
    [Fact]
    public async Task Tenant_owned_data_is_invisible_to_other_tenants()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var aliceInvite = await alice.Client.InviteAsync(TenantRole.Member);

        var bobInvites = await bob.Client.GetJsonAsync<List<InvitationResponse>>("/api/v1/tenants/current/invitations");
        var revokeOther = await bob.Client.DeleteAsync($"/api/v1/tenants/current/invitations/{aliceInvite.Id}");
        var bobAudit = await bob.Client.GetJsonAsync<List<AuditEventResponse>>("/api/v1/tenants/current/audit-events");

        Assert.DoesNotContain(bobInvites, i => i.Id == aliceInvite.Id);
        await revokeOther.EnsureStatusAsync(HttpStatusCode.NotFound);
        Assert.DoesNotContain(bobAudit, e => e.TargetId == aliceInvite.Id);
        Assert.All(bobAudit, e => Assert.NotEqual(alice.UserId, e.ActorUserId));

        var aliceAudit = await alice.Client.GetJsonAsync<List<AuditEventResponse>>("/api/v1/tenants/current/audit-events");
        Assert.Contains(aliceAudit, e => e.Action == AuditActions.InvitationCreated && e.TargetId == aliceInvite.Id);
    }

    [Fact]
    public async Task Invitation_flow_grants_membership_and_switch_changes_active_tenant()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();

        var bobInAlice = await factory.JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Member);

        var me = await bobInAlice.GetJsonAsync<MeResponse>("/api/v1/me");
        Assert.Equal(alice.TenantId, me.Tenant.Id);
        Assert.Equal(TenantRole.Member, me.Tenant.Role);

        // The token issued for Bob's previous tenant stopped working when he switched.
        await (await bob.Client.GetAsync("/api/v1/me")).EnsureStatusAsync(HttpStatusCode.Unauthorized);

        var bobTenants = await bobInAlice.GetJsonAsync<List<TenantSummary>>("/api/v1/tenants");
        Assert.Equal(2, bobTenants.Count);

        // Members cannot manage invitations.
        var invite = await bobInAlice.PostJsonAsync("/api/v1/tenants/current/invitations", new { email = ApiClient.UniqueEmail(), role = TenantRole.Viewer });
        await invite.EnsureStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Invitation_tokens_are_single_use()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var carol = await factory.NewUserAsync();
        var invitation = await alice.Client.InviteAsync(TenantRole.Viewer);

        await (await bob.Client.PostJsonAsync("/api/v1/invitations/accept", new { token = invitation.Token })).EnsureStatusAsync(HttpStatusCode.OK);
        var second = await carol.Client.PostJsonAsync("/api/v1/invitations/accept", new { token = invitation.Token });

        await second.EnsureStatusAsync(HttpStatusCode.NotFound);
        Assert.Equal("invitation_invalid", await second.ProblemCodeAsync());
    }

    [Fact]
    public async Task Switching_to_a_tenant_without_membership_is_forbidden()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();

        var response = await bob.Client.PostJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = alice.TenantId });

        await response.EnsureStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admins_cannot_grant_owner_or_invite_admins()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var carol = await factory.NewUserAsync();
        var bobAsAdmin = await factory.JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Admin);
        await factory.JoinAndSwitchAsync(alice.Client, carol.Client, TenantRole.Member);

        var promote = await bobAsAdmin.PatchJsonAsync($"/api/v1/tenants/current/members/{carol.UserId}", new { role = TenantRole.Owner });
        var inviteAdmin = await bobAsAdmin.PostJsonAsync("/api/v1/tenants/current/invitations", new { email = ApiClient.UniqueEmail(), role = TenantRole.Admin });
        var demoteOwner = await bobAsAdmin.PatchJsonAsync($"/api/v1/tenants/current/members/{alice.UserId}", new { role = TenantRole.Member });
        var allowed = await bobAsAdmin.PatchJsonAsync($"/api/v1/tenants/current/members/{carol.UserId}", new { role = TenantRole.Viewer });

        await promote.EnsureStatusAsync(HttpStatusCode.Forbidden);
        await inviteAdmin.EnsureStatusAsync(HttpStatusCode.Forbidden);
        await demoteOwner.EnsureStatusAsync(HttpStatusCode.Forbidden);
        await allowed.EnsureStatusAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Last_owner_cannot_leave_or_be_demoted()
    {
        var alice = await factory.NewUserAsync();

        var leave = await alice.Client.DeleteAsync($"/api/v1/tenants/current/members/{alice.UserId}");
        var demote = await alice.Client.PatchJsonAsync($"/api/v1/tenants/current/members/{alice.UserId}", new { role = TenantRole.Admin });

        await leave.EnsureStatusAsync(HttpStatusCode.Conflict);
        await demote.EnsureStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("last_owner", await leave.ProblemCodeAsync());
    }

    [Fact]
    public async Task Removed_member_loses_access_immediately()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var bobInAlice = await factory.JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Member);

        await (await alice.Client.DeleteAsync($"/api/v1/tenants/current/members/{bob.UserId}")).EnsureStatusAsync(HttpStatusCode.NoContent);

        await (await bobInAlice.GetAsync("/api/v1/me")).EnsureStatusAsync(HttpStatusCode.Unauthorized);
    }
}
