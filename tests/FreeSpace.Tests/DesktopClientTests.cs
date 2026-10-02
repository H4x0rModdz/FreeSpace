using System.Reflection;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Desktop.Core.Transfers;
using FreeSpace.Domain.Storage;
using FreeSpace.Tests.Infrastructure;

namespace FreeSpace.Tests;

/// <summary>The desktop app's client and transfer engines, against the real API.</summary>
[Collection(ApiCollection.Name)]
public sealed class DesktopClientTests(ApiFactory factory) : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private FreeSpaceClient NewClient(ISessionStore? store = null) =>
        new(factory.Server.BaseAddress, store ?? new InMemorySessionStore(), factory.Server.CreateHandler());

    private static async Task<FreeSpaceClient> SignedUpAsync(FreeSpaceClient client)
    {
        await client.RegisterAsync("Desk", ApiClient.UniqueEmail("desk"), ApiClient.Password);
        return client;
    }

    private async Task ConnectS3Async(FreeSpaceClient client) =>
        await client.ConnectS3Async(new ConnectS3Request("S3", factory.S3.GetConnectionString(), "us-east-1", await factory.S3.CreateBucketAsync(),
            null, S3TestServer.AccessKey, S3TestServer.SecretKey, null, null));

    private string TempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"freespace-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"freespace-test-{Guid.NewGuid():N}.out");
        _tempFiles.Add(path);
        _tempFiles.Add(path + ".part");
        return path;
    }

    private static void BreakAccessToken(FreeSpaceClient client) =>
        typeof(FreeSpaceClient).GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, "garbage");

    private static byte[] RandomContent(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task Session_survives_restart_through_the_store_and_logout_clears_it()
    {
        var store = new InMemorySessionStore();
        using var first = await SignedUpAsync(NewClient(store));
        var me = await first.MeAsync();

        using var restarted = NewClient(store);
        Assert.True(await restarted.TryResumeAsync());
        Assert.Equal(me.User.Id, (await restarted.MeAsync()).User.Id);

        var ended = false;
        restarted.SessionEnded += (_, _) => ended = true;
        await restarted.LogoutAsync();

        Assert.True(ended);
        Assert.Null(store.Load());
        using var afterLogout = NewClient(store);
        Assert.False(await afterLogout.TryResumeAsync());
    }

    [Fact]
    public async Task Expired_access_tokens_are_refreshed_once_even_under_concurrency()
    {
        using var client = await SignedUpAsync(NewClient());
        BreakAccessToken(client);

        // Five calls hit 401 together; a naive client would refresh five times and the server would
        // treat the replayed refresh tokens as theft and revoke the session.
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.MeAsync()));

        Assert.All(results, r => Assert.Equal(results[0].User.Id, r.User.Id));
        Assert.True(client.IsSignedIn);
        Assert.NotNull(await client.MeAsync());
    }

    [Fact]
    public async Task Api_errors_surface_their_code()
    {
        using var client = await SignedUpAsync(NewClient());
        await client.CreateFolderAsync(null, "Docs");

        var error = await Assert.ThrowsAsync<ApiException>(() => client.CreateFolderAsync(null, "docs"));

        Assert.Equal("name_conflict", error.Code);
    }

    [Fact]
    public async Task Direct_s3_upload_and_resumable_download_round_trip()
    {
        using var client = await SignedUpAsync(NewClient());
        await ConnectS3Async(client);
        var content = RandomContent(9 << 20); // two chunks
        var reported = new List<long>();

        using var providerHttp = new HttpClient();
        var node = await new UploadEngine(client, providerHttp)
            .UploadAsync(UploadSource.FromFile(TempFile(content)), parentId: null, new SyncProgress(reported.Add));

        // Simulate an interrupted download: half the file is already on disk.
        var destination = TempPath();
        await File.WriteAllBytesAsync(destination + ".part", content[..(content.Length / 2)]);
        await new DownloadEngine(client).DownloadAsync(node.Id, node.SizeBytes, destination);

        Assert.Equal(content.Length, reported.Last());
        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task Upload_through_the_api_works_for_google_drive()
    {
        using var client = await SignedUpAsync(NewClient());
        var me = await client.MeAsync();
        await factory.ConnectGoogleAsync(new TestUser(factory.CreateClient(), null!, me.User.Id, me.Tenant.Id) with
        {
            Client = factory.CreateClient().WithToken(AccessTokenOf(client)),
        });
        var content = RandomContent(3 << 20);

        using var providerHttp = new HttpClient();
        var node = await new UploadEngine(client, providerHttp)
            .UploadAsync(UploadSource.FromFile(TempFile(content)), null, route: UploadRoute.ThroughApi);

        Assert.Contains(factory.Google.Files.Values, f => f.Content.SequenceEqual(content));
        Assert.Equal(StorageProvider.GoogleDrive, (await client.StorageAccountsAsync()).Single().Provider);
        Assert.Equal(content.Length, node.SizeBytes);
    }

    [Fact]
    public async Task Failed_uploads_release_their_reservation()
    {
        using var client = await SignedUpAsync(NewClient());
        await ConnectS3Async(client);
        var source = new UploadSource("broken.bin", 1 << 20, () => throw new IOException("disk unplugged"));

        using var providerHttp = new HttpClient();
        await Assert.ThrowsAnyAsync<Exception>(() => new UploadEngine(client, providerHttp).UploadAsync(source, null));

        var account = (await client.StorageAccountsAsync()).Single();
        Assert.Equal(0, (await factory.LoadAccountAsync(account.Id)).ReservedBytes);
    }

    private static string AccessTokenOf(FreeSpaceClient client) =>
        (string)typeof(FreeSpaceClient).GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need the values synchronously.</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles.Where(File.Exists)) File.Delete(file);
    }
}
