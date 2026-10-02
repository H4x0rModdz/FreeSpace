using System.Net;
using FreeSpace.Api.StorageAccounts;
using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FreeSpace.Tests.Infrastructure;

public static class StorageTestHelpers
{
    /// <summary>Connects a fresh bucket on the S3 test server to the user's active tenant.</summary>
    public static async Task<StorageAccountResponse> ConnectS3Async(this ApiFactory factory, TestUser user, long? quotaBytes = null, string displayName = "S3")
    {
        var bucket = await factory.S3.CreateBucketAsync();
        var response = await user.Client.PostJsonAsync("/api/v1/storage-accounts/s3", new
        {
            displayName, endpoint = factory.S3.GetConnectionString(), region = "us-east-1", bucket,
            accessKeyId = S3TestServer.AccessKey, secretAccessKey = S3TestServer.SecretKey, quotaBytes,
        });
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return await response.ReadAsync<StorageAccountResponse>();
    }

    /// <summary>Runs the Google consent round-trip against the fake and returns the connected account.</summary>
    public static async Task<StorageAccountResponse> ConnectGoogleAsync(this ApiFactory factory, TestUser user)
    {
        await (await user.Client.PostAsync("/api/v1/storage-accounts/google/authorize", null)).EnsureStatusAsync(HttpStatusCode.OK);
        var code = factory.Google.Consent($"sub-{Guid.NewGuid():N}", "drive@gmail.com");
        var callback = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var result = await callback.GetAsync($"/api/v1/storage-accounts/google/callback?code={code}&state={Uri.EscapeDataString(factory.Google.LastState!)}");
        await result.EnsureStatusAsync(HttpStatusCode.OK);
        return (await user.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"))
            .Single(a => a.Provider == StorageProvider.GoogleDrive);
    }

    /// <summary>Reads an account row directly (to see counters the API does not expose, like reservations).</summary>
    public static async Task<StorageAccount> LoadAccountAsync(this ApiFactory factory, Guid accountId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StorageAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == accountId);
    }

    public static async Task WithDbAsync(this ApiFactory factory, Func<AppDbContext, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
