using System.Net;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace FreeSpace.Tests;

[Collection(ApiCollection.Name)]
public sealed class ApiSurfaceTests(ApiFactory factory)
{
    [Fact]
    public async Task Api_docs_expose_openapi_with_bearer_auth_and_scalar_when_enabled()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("ApiDocs:Enabled", "true")).CreateClient();

        var openApi = await client.GetStringAsync("/openapi/v1.json");
        var scalar = await client.GetAsync("/scalar");

        Assert.Contains("/api/v1/auth/login", openApi);
        Assert.Contains("/api/v1/storage-accounts", openApi);
        Assert.Contains("\"bearer\"", openApi);
        await scalar.EnsureStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_docs_are_off_by_default_outside_development()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Minimum_role_violations_answer_with_problem_details_code()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var bobAsViewer = await factory.JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Viewer);

        var response = await bobAsViewer.GetAsync("/api/v1/tenants/current/audit-events");

        await response.EnsureStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal("insufficient_role", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Cors_allows_only_the_configured_frontend()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("App:FrontendUrl", "https://app.example.com/some/path")).CreateClient();

        async Task<string?> AllowedOriginFor(string origin)
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/me");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization");
            var response = await client.SendAsync(preflight);
            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;
        }

        Assert.Equal("https://app.example.com", await AllowedOriginFor("https://app.example.com"));
        Assert.Null(await AllowedOriginFor("https://evil.example.com"));
    }

    [Fact]
    public async Task Validation_errors_carry_a_code()
    {
        var response = await factory.CreateClient().PostJsonAsync("/api/v1/auth/login", new { email = "" });

        await response.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("validation_failed", await response.ProblemCodeAsync());
    }
}
