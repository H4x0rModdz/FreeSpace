using System.Net;
using FreeSpace.Api.Auth;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace FreeSpace.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuthTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_creates_user_with_owned_personal_tenant()
    {
        var client = factory.CreateClient();
        var email = ApiClient.UniqueEmail();
        var tokens = await client.RegisterAsync(email, name: "Ana");

        var me = await client.WithToken(tokens.AccessToken).GetJsonAsync<MeResponse>("/api/v1/me");

        Assert.Equal(email, me.User.Email);
        Assert.Equal("Ana's space", me.Tenant.Name);
        Assert.Equal(TenantRole.Owner, me.Tenant.Role);
    }

    [Fact]
    public async Task Register_rejects_duplicate_email_case_insensitively()
    {
        var client = factory.CreateClient();
        var email = ApiClient.UniqueEmail();
        await client.RegisterAsync(email);

        var response = await client.PostJsonAsync("/api/v1/auth/register", new { name = "X", email = email.ToUpperInvariant(), password = ApiClient.Password });

        await response.EnsureStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("email_taken", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Register_validates_payload()
    {
        var response = await factory.CreateClient().PostJsonAsync("/api/v1/auth/register", new { name = "X", email = "not-an-email", password = "short" });

        await response.EnsureStatusAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_can_be_disabled()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("Auth:AllowRegistration", "false")).CreateClient();

        var response = await client.PostJsonAsync("/api/v1/auth/register", new { name = "X", email = ApiClient.UniqueEmail(), password = ApiClient.Password });

        await response.EnsureStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal("registration_disabled", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Login_with_wrong_password_or_unknown_email_is_401()
    {
        var client = factory.CreateClient();
        var email = ApiClient.UniqueEmail();
        await client.RegisterAsync(email);

        var wrongPassword = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = "wrong-password-123" });
        var unknownEmail = await client.PostJsonAsync("/api/v1/auth/login", new { email = ApiClient.UniqueEmail(), password = ApiClient.Password });

        await wrongPassword.EnsureStatusAsync(HttpStatusCode.Unauthorized);
        await unknownEmail.EnsureStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("invalid_credentials", await wrongPassword.ProblemCodeAsync());
        Assert.Equal("invalid_credentials", await unknownEmail.ProblemCodeAsync());
    }

    [Fact]
    public async Task Login_returns_working_tokens()
    {
        var client = factory.CreateClient();
        var email = ApiClient.UniqueEmail();
        await client.RegisterAsync(email);

        var response = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = ApiClient.Password });
        await response.EnsureStatusAsync(HttpStatusCode.OK);
        var tokens = await response.ReadAsync<TokenPair>();

        var me = await factory.CreateClient().WithToken(tokens.AccessToken).GetJsonAsync<MeResponse>("/api/v1/me");
        Assert.Equal(email, me.User.Email);
    }

    [Fact]
    public async Task Protected_endpoints_require_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/me");

        await response.EnsureStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_reuse_revokes_the_session()
    {
        var client = factory.CreateClient();
        var first = await client.RegisterAsync();

        var rotated = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        await rotated.EnsureStatusAsync(HttpStatusCode.OK);
        var second = await rotated.ReadAsync<TokenPair>();
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Replaying the rotated-out token is treated as theft...
        var replay = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        await replay.EnsureStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("refresh_token_reused", await replay.ProblemCodeAsync());

        // ...which kills the whole session, including the legitimate latest tokens.
        var afterRevoke = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = second.RefreshToken });
        await afterRevoke.EnsureStatusAsync(HttpStatusCode.Unauthorized);
        var me = await factory.CreateClient().WithToken(second.AccessToken).GetAsync("/api/v1/me");
        await me.EnsureStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rejects_garbage_tokens()
    {
        var response = await factory.CreateClient().PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = "not-a-token" });

        await response.EnsureStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("invalid_refresh_token", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Logout_revokes_access_and_refresh_tokens_immediately()
    {
        var tokens = await factory.CreateClient().RegisterAsync();
        var client = factory.CreateClient().WithToken(tokens.AccessToken);

        await (await client.PostAsync("/api/v1/auth/logout", null)).EnsureStatusAsync(HttpStatusCode.NoContent);

        await (await client.GetAsync("/api/v1/me")).EnsureStatusAsync(HttpStatusCode.Unauthorized);
        var refresh = await factory.CreateClient().PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        await refresh.EnsureStatusAsync(HttpStatusCode.Unauthorized);
    }
}
