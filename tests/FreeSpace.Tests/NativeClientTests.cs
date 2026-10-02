using System.Net;
using FreeSpace.Api.Common;
using FreeSpace.Api.StorageAccounts;
using FreeSpace.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FreeSpace.Tests;

[Collection(ApiCollection.Name)]
public sealed class NativeClientTests(ApiFactory factory)
{
    [Theory]
    [InlineData("http://127.0.0.1:53682/callback", true)]
    [InlineData("http://127.0.0.1/", true)]
    [InlineData("http://[::1]:8000/oauth", true)]
    [InlineData("freespace://oauth/done", true)]
    [InlineData("FreeSpace://oauth", true)]
    [InlineData("http://localhost:53682/callback", false)]       // name, not IP: can be re-pointed
    [InlineData("http://localhost.evil.com/callback", false)]
    [InlineData("http://192.168.0.10:8080/", false)]
    [InlineData("https://127.0.0.1/", false)]                    // loopback redirects are plain http by convention
    [InlineData("https://evil.com/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("otherapp://oauth", false)]
    [InlineData("http://user@127.0.0.1/", false)]
    [InlineData("not a url", false)]
    public void Native_redirects_allow_only_loopback_and_registered_schemes(string url, bool allowed) =>
        Assert.Equal(allowed, NativeRedirects.IsAllowed(url, ["freespace"]));

    private async Task<HttpResponseMessage> AuthorizeAsync(HttpClient client, string? returnUrl) =>
        await client.PostJsonAsync("/api/v1/storage-accounts/google/authorize", new { returnUrl });

    private HttpClient Browser() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Google_connect_returns_to_the_desktop_app_loopback()
    {
        var alice = await factory.NewUserAsync();
        await (await AuthorizeAsync(alice.Client, "http://127.0.0.1:53682/callback")).EnsureStatusAsync(HttpStatusCode.OK);
        var code = factory.Google.Consent($"sub-{Guid.NewGuid():N}", "desk@gmail.com");

        var callback = await Browser().GetAsync($"/api/v1/storage-accounts/google/callback?code={code}&state={Uri.EscapeDataString(factory.Google.LastState!)}");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var location = callback.Headers.Location!.ToString();
        var account = Assert.Single(await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"));
        Assert.Equal($"http://127.0.0.1:53682/callback?status=connected&accountId={account.Id}", location);
    }

    [Fact]
    public async Task Custom_scheme_keeps_its_query_and_failures_go_back_too()
    {
        var alice = await factory.NewUserAsync();
        await (await AuthorizeAsync(alice.Client, "freespace://oauth/done?attempt=7")).EnsureStatusAsync(HttpStatusCode.OK);

        var denied = await Browser().GetAsync($"/api/v1/storage-accounts/google/callback?error=access_denied&state={Uri.EscapeDataString(factory.Google.LastState!)}");

        Assert.Equal("freespace://oauth/done?attempt=7&status=access_denied", denied.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Arbitrary_return_urls_are_refused()
    {
        var alice = await factory.NewUserAsync();

        var response = await AuthorizeAsync(alice.Client, "https://evil.com/steal");

        await response.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("invalid_return_url", await response.ProblemCodeAsync());
    }
}
