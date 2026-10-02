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
    public async Task Validation_errors_carry_a_code()
    {
        var response = await factory.CreateClient().PostJsonAsync("/api/v1/auth/login", new { email = "" });

        await response.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("validation_failed", await response.ProblemCodeAsync());
    }
}
