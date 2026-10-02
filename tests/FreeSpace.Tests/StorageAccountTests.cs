using System.Net;
using System.Text.Json;
using FreeSpace.Api.StorageAccounts;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FreeSpace.Tests;

[Collection(ApiCollection.Name)]
public sealed class StorageAccountTests(ApiFactory factory)
{
    private HttpClient AnonymousClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Runs the full browser round-trip: authorize → consent at Google → callback.</summary>
    private async Task<HttpResponseMessage> ConnectGoogleAsync(HttpClient admin, string subject, string email, string? refreshToken = null)
    {
        var authorize = await admin.PostAsync("/api/v1/storage-accounts/google/authorize", null);
        await authorize.EnsureStatusAsync(HttpStatusCode.OK);
        var state = factory.Google.LastState!;
        var code = factory.Google.Consent(subject, email, refreshToken);
        return await AnonymousClient().GetAsync($"/api/v1/storage-accounts/google/callback?code={code}&state={Uri.EscapeDataString(state)}");
    }

    private Task<string> CreateBucketAsync() => factory.S3.CreateBucketAsync();

    private object S3Request(string bucket, string? secret = null, string? endpoint = null) => new
    {
        displayName = "Test S3",
        endpoint = endpoint ?? factory.S3.GetConnectionString(),
        region = "us-east-1",
        bucket,
        accessKeyId = S3TestServer.AccessKey,
        secretAccessKey = secret ?? S3TestServer.SecretKey,
        quotaBytes = 10L << 30,
    };

    [Fact]
    public async Task Google_connect_flow_creates_account_with_quota_and_hides_credentials()
    {
        var alice = await factory.NewUserAsync();
        const string refreshToken = "refresh-secret-value";

        var callback = await ConnectGoogleAsync(alice.Client, $"sub-{Guid.NewGuid():N}", "alice@gmail.com", refreshToken);
        await callback.EnsureStatusAsync(HttpStatusCode.OK);

        var listResponse = await alice.Client.GetAsync("/api/v1/storage-accounts");
        var raw = await listResponse.Content.ReadAsStringAsync();
        var accounts = JsonSerializer.Deserialize<List<StorageAccountResponse>>(raw, ApiClient.Json)!;

        var account = Assert.Single(accounts);
        Assert.Equal(StorageProvider.GoogleDrive, account.Provider);
        Assert.Equal(StorageAccountStatus.Active, account.Status);
        Assert.Equal("alice@gmail.com", account.Email);
        Assert.Equal(15L << 30, account.TotalBytes);
        Assert.Equal(1L << 30, account.UsedBytes);
        Assert.DoesNotContain(refreshToken, raw);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconnecting_the_same_google_account_updates_it_and_recovers_from_revocation()
    {
        var alice = await factory.NewUserAsync();
        var subject = $"sub-{Guid.NewGuid():N}";
        var firstToken = $"refresh-{Guid.NewGuid():N}";
        await (await ConnectGoogleAsync(alice.Client, subject, "a@gmail.com", firstToken)).EnsureStatusAsync(HttpStatusCode.OK);
        var id = (await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts")).Single().Id;

        // The user revokes access at Google: the next sync flags the account.
        factory.Google.RevokedTokens[firstToken] = true;
        var synced = await (await alice.Client.PostAsync($"/api/v1/storage-accounts/{id}/sync", null)).ReadAsync<StorageAccountResponse>();
        Assert.Equal(StorageAccountStatus.NeedsReauth, synced.Status);
        Assert.NotNull(synced.LastError);

        await (await ConnectGoogleAsync(alice.Client, subject, "a@gmail.com")).EnsureStatusAsync(HttpStatusCode.OK);

        var after = Assert.Single(await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"));
        Assert.Equal(id, after.Id);
        Assert.Equal(StorageAccountStatus.Active, after.Status);
        Assert.Null(after.LastError);
    }

    [Fact]
    public async Task Oauth_state_is_single_use_and_denials_are_reported()
    {
        var alice = await factory.NewUserAsync();
        await (await alice.Client.PostAsync("/api/v1/storage-accounts/google/authorize", null)).EnsureStatusAsync(HttpStatusCode.OK);
        var state = Uri.EscapeDataString(factory.Google.LastState!);

        var denied = await AnonymousClient().GetAsync($"/api/v1/storage-accounts/google/callback?error=access_denied&state={state}");
        var replay = await AnonymousClient().GetAsync($"/api/v1/storage-accounts/google/callback?code={factory.Google.Consent("s", "e@x.com")}&state={state}");
        var forged = await AnonymousClient().GetAsync($"/api/v1/storage-accounts/google/callback?code=x&state=forged");

        Assert.Equal("access_denied", await denied.ProblemCodeAsync());
        Assert.Equal("invalid_state", await replay.ProblemCodeAsync());
        Assert.Equal("invalid_state", await forged.ProblemCodeAsync());
        Assert.Empty(await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"));
    }

    [Fact]
    public async Task Callback_redirects_to_frontend_when_configured()
    {
        var alice = await factory.NewUserAsync();
        await (await alice.Client.PostAsync("/api/v1/storage-accounts/google/authorize", null)).EnsureStatusAsync(HttpStatusCode.OK);
        var state = Uri.EscapeDataString(factory.Google.LastState!);
        var client = factory.WithWebHostBuilder(b => b.UseSetting("App:FrontendUrl", "https://app.example.com"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/api/v1/storage-accounts/google/callback?code={factory.Google.Consent($"sub-{Guid.NewGuid():N}", "r@x.com")}&state={state}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://app.example.com/storage/google/callback?status=connected&accountId=", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Members_cannot_connect_or_remove_storage_and_tenants_are_isolated()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var carol = await factory.NewUserAsync();
        await (await ConnectGoogleAsync(alice.Client, $"sub-{Guid.NewGuid():N}", "a@gmail.com")).EnsureStatusAsync(HttpStatusCode.OK);
        var id = (await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts")).Single().Id;
        var bobAsMember = await factory.JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Member);

        // Members see the tenant's storage but cannot change it.
        Assert.Single(await bobAsMember.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"));
        await (await bobAsMember.PostAsync("/api/v1/storage-accounts/google/authorize", null)).EnsureStatusAsync(HttpStatusCode.Forbidden);
        await (await bobAsMember.DeleteAsync($"/api/v1/storage-accounts/{id}")).EnsureStatusAsync(HttpStatusCode.Forbidden);

        // Other tenants see nothing, even by id.
        Assert.Empty(await carol.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"));
        await (await carol.Client.GetAsync($"/api/v1/storage-accounts/{id}")).EnsureStatusAsync(HttpStatusCode.NotFound);
        await (await carol.Client.DeleteAsync($"/api/v1/storage-accounts/{id}")).EnsureStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Removing_a_google_account_revokes_the_grant()
    {
        var alice = await factory.NewUserAsync();
        var token = $"refresh-{Guid.NewGuid():N}";
        await (await ConnectGoogleAsync(alice.Client, $"sub-{Guid.NewGuid():N}", "a@gmail.com", token)).EnsureStatusAsync(HttpStatusCode.OK);
        var id = (await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts")).Single().Id;

        await (await alice.Client.DeleteAsync($"/api/v1/storage-accounts/{id}")).EnsureStatusAsync(HttpStatusCode.NoContent);

        Assert.True(factory.Google.RevokedTokens.ContainsKey(token));
        Assert.Empty(await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"));
    }

    [Fact]
    public async Task S3_connect_verifies_credentials_and_encrypts_them_at_rest()
    {
        var alice = await factory.NewUserAsync();
        var bucket = await CreateBucketAsync();

        var response = await alice.Client.PostJsonAsync("/api/v1/storage-accounts/s3", S3Request(bucket));

        await response.EnsureStatusAsync(HttpStatusCode.Created);
        var account = await response.ReadAsync<StorageAccountResponse>();
        Assert.Equal(StorageProvider.S3, account.Provider);
        Assert.Equal(10L << 30, account.TotalBytes);
        Assert.Equal(bucket, account.Config!.Value.GetProperty("bucket").GetString());
        Assert.False(account.Config.Value.TryGetProperty("secretAccessKey", out _));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StorageAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == account.Id);
        Assert.StartsWith("v1:", row.SecretCiphertext);
        Assert.DoesNotContain(S3TestServer.SecretKey, row.SecretCiphertext);

        var sync = await alice.Client.PostAsync($"/api/v1/storage-accounts/{account.Id}/sync", null);
        Assert.Equal(StorageAccountStatus.Active, (await sync.ReadAsync<StorageAccountResponse>()).Status);
    }

    [Fact]
    public async Task S3_connect_with_wrong_credentials_fails()
    {
        var alice = await factory.NewUserAsync();
        var bucket = await CreateBucketAsync();

        var response = await alice.Client.PostJsonAsync("/api/v1/storage-accounts/s3", S3Request(bucket, secret: "wrong-secret-key"));

        await response.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("s3_connection_failed", await response.ProblemCodeAsync());
        Assert.Empty(await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"));
    }

    [Fact]
    public async Task S3_endpoints_on_private_networks_are_blocked_by_default()
    {
        var strict = factory.WithWebHostBuilder(b => b
            .UseSetting("Storage:S3:AllowPrivateEndpoints", "false")
            .UseSetting("Storage:S3:AllowInsecureEndpoints", "true"));
        var tokens = await strict.CreateClient().RegisterAsync();
        var client = strict.CreateClient().WithToken(tokens.AccessToken);
        var bucket = await CreateBucketAsync();
        var port = new Uri(factory.S3.GetConnectionString()).Port;

        // Literal private IPs are rejected up front...
        var literal = await client.PostJsonAsync("/api/v1/storage-accounts/s3", S3Request(bucket, endpoint: $"http://127.0.0.1:{port}"));
        // ...and hostnames that resolve to one are blocked at connect time (covers DNS rebinding).
        var hostname = await client.PostJsonAsync("/api/v1/storage-accounts/s3", S3Request(bucket, endpoint: $"http://localhost:{port}"));
        var metadata = await client.PostJsonAsync("/api/v1/storage-accounts/s3", S3Request(bucket, endpoint: "http://169.254.169.254"));

        Assert.Equal("endpoint_not_allowed", await literal.ProblemCodeAsync());
        Assert.Equal("s3_connection_failed", await hostname.ProblemCodeAsync());
        Assert.Contains("non-public", await hostname.Content.ReadAsStringAsync());
        Assert.Equal("endpoint_not_allowed", await metadata.ProblemCodeAsync());
    }

    [Fact]
    public async Task Summary_adds_up_active_accounts()
    {
        var alice = await factory.NewUserAsync();
        await (await ConnectGoogleAsync(alice.Client, $"sub-{Guid.NewGuid():N}", "a@gmail.com")).EnsureStatusAsync(HttpStatusCode.OK);
        await (await alice.Client.PostJsonAsync("/api/v1/storage-accounts/s3", S3Request(await CreateBucketAsync()))).EnsureStatusAsync(HttpStatusCode.Created);

        var summary = await alice.Client.GetJsonAsync<StorageSummaryResponse>("/api/v1/storage-accounts/summary");

        Assert.Equal(2, summary.ActiveAccounts);
        Assert.Equal((15L << 30) + (10L << 30), summary.TotalBytes);
        Assert.Equal(1L << 30, summary.UsedBytes);
    }
}
