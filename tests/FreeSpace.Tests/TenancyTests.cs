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
    private async Task<(HttpClient Client, TokenPair Tokens, Guid UserId, Guid TenantId)> NewUserAsync()
    {
        var tokens = await factory.CreateClient().RegisterAsync();
        var client = factory.CreateClient().WithToken(tokens.AccessToken);
        var me = await client.GetJsonAsync<MeResponse>("/api/v1/me");
        return (client, tokens, me.User.Id, me.Tenant.Id);
    }

    private static async Task<CreatedInvitationResponse> InviteAsync(HttpClient client, TenantRole role)
    {
        var response = await client.PostJsonAsync("/api/v1/tenants/current/invitations", new { email = ApiClient.UniqueEmail("invitee"), role });
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return await response.ReadAsync<CreatedInvitationResponse>();
    }

    /// <summary>Joins <paramref name="joiner"/> into the inviter's tenant and switches the joiner's session to it.</summary>
    private async Task<HttpClient> JoinAndSwitchAsync(HttpClient inviter, HttpClient joiner, TenantRole role)
    {
        var invitation = await InviteAsync(inviter, role);
        var accepted = await joiner.PostJsonAsync("/api/v1/invitations/accept", new { token = invitation.Token });
        await accepted.EnsureStatusAsync(HttpStatusCode.OK);
        var tenantId = (await accepted.ReadAsync<AcceptedInvitationResponse>()).TenantId;

        var switched = await joiner.PostJsonAsync("/api/v1/auth/switch-tenant", new { tenantId });
        await switched.EnsureStatusAsync(HttpStatusCode.OK);
        return factory.CreateClient().WithToken((await switched.ReadAsync<AccessTokenResponse>()).AccessToken);
    }

    [Fact]
    public async Task Tenant_owned_data_is_invisible_to_other_tenants()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var aliceInvite = await InviteAsync(alice.Client, TenantRole.Member);

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
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();

        var bobInAlice = await JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Member);

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
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var carol = await NewUserAsync();
        var invitation = await InviteAsync(alice.Client, TenantRole.Viewer);

        await (await bob.Client.PostJsonAsync("/api/v1/invitations/accept", new { token = invitation.Token })).EnsureStatusAsync(HttpStatusCode.OK);
        var second = await carol.Client.PostJsonAsync("/api/v1/invitations/accept", new { token = invitation.Token });

        await second.EnsureStatusAsync(HttpStatusCode.NotFound);
        Assert.Equal("invitation_invalid", await second.ProblemCodeAsync());
    }

    [Fact]
    public async Task Switching_to_a_tenant_without_membership_is_forbidden()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();

        var response = await bob.Client.PostJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = alice.TenantId });

        await response.EnsureStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admins_cannot_grant_owner_or_invite_admins()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var carol = await NewUserAsync();
        var bobAsAdmin = await JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Admin);
        await JoinAndSwitchAsync(alice.Client, carol.Client, TenantRole.Member);

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
        var alice = await NewUserAsync();

        var leave = await alice.Client.DeleteAsync($"/api/v1/tenants/current/members/{alice.UserId}");
        var demote = await alice.Client.PatchJsonAsync($"/api/v1/tenants/current/members/{alice.UserId}", new { role = TenantRole.Admin });

        await leave.EnsureStatusAsync(HttpStatusCode.Conflict);
        await demote.EnsureStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("last_owner", await leave.ProblemCodeAsync());
    }

    [Fact]
    public async Task Removed_member_loses_access_immediately()
    {
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var bobInAlice = await JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Member);

        await (await alice.Client.DeleteAsync($"/api/v1/tenants/current/members/{bob.UserId}")).EnsureStatusAsync(HttpStatusCode.NoContent);

        await (await bobInAlice.GetAsync("/api/v1/me")).EnsureStatusAsync(HttpStatusCode.Unauthorized);
    }
}
