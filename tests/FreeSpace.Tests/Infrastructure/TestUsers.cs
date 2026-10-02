using System.Net;
using FreeSpace.Api.Auth;
using FreeSpace.Api.Tenants;
using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Tests.Infrastructure;

public sealed record TestUser(HttpClient Client, TokenPair Tokens, Guid UserId, Guid TenantId);

public static class TestUsers
{
    /// <summary>Registers a user; the returned client is authenticated as the owner of their personal tenant.</summary>
    public static async Task<TestUser> NewUserAsync(this ApiFactory factory)
    {
        var tokens = await factory.CreateClient().RegisterAsync();
        var client = factory.CreateClient().WithToken(tokens.AccessToken);
        var me = await client.GetJsonAsync<MeResponse>("/api/v1/me");
        return new TestUser(client, tokens, me.User.Id, me.Tenant.Id);
    }

    public static async Task<CreatedInvitationResponse> InviteAsync(this HttpClient client, TenantRole role)
    {
        var response = await client.PostJsonAsync("/api/v1/tenants/current/invitations", new { email = ApiClient.UniqueEmail("invitee"), role });
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return await response.ReadAsync<CreatedInvitationResponse>();
    }

    /// <summary>Joins <paramref name="joiner"/> into the inviter's tenant and returns a client switched to it.</summary>
    public static async Task<HttpClient> JoinAndSwitchAsync(this ApiFactory factory, HttpClient inviter, HttpClient joiner, TenantRole role)
    {
        var invitation = await inviter.InviteAsync(role);
        var accepted = await joiner.PostJsonAsync("/api/v1/invitations/accept", new { token = invitation.Token });
        await accepted.EnsureStatusAsync(HttpStatusCode.OK);
        var tenantId = (await accepted.ReadAsync<AcceptedInvitationResponse>()).TenantId;

        var switched = await joiner.PostJsonAsync("/api/v1/auth/switch-tenant", new { tenantId });
        await switched.EnsureStatusAsync(HttpStatusCode.OK);
        return factory.CreateClient().WithToken((await switched.ReadAsync<AccessTokenResponse>()).AccessToken);
    }
}
