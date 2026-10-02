using System.Net;
using System.Net.Http.Headers;
using FreeSpace.Api.Files;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FreeSpace.Tests;

[Collection(ApiCollection.Name)]
public sealed class UploadTests(ApiFactory factory)
{
    private const long MiB = 1L << 20;

    /// <summary>9 MiB = two chunks (8 MiB + 1 MiB) with the minimum chunk size.</summary>
    private static byte[] RandomContent(long size = 9 * MiB)
    {
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static async Task<UploadResponse> StartAsync(HttpClient client, string name, long size, Guid? parentId = null)
    {
        var response = await client.PostJsonAsync("/api/v1/uploads", new { fileName = name, sizeBytes = size, mimeType = "application/octet-stream", parentId });
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return await response.ReadAsync<UploadResponse>();
    }

    private static ByteArrayContent Chunk(byte[] content, UploadResponse upload, int index)
    {
        var offset = (int)(index * upload.ChunkSize);
        var length = (int)Math.Min(upload.ChunkSize, content.Length - offset);
        return new ByteArrayContent(content, offset, length);
    }

    private static async Task PutChunkViaApiAsync(HttpClient client, UploadResponse upload, byte[] content, int index) =>
        await (await client.PutAsync($"/api/v1/uploads/{upload.Id}/chunks/{index}", Chunk(content, upload, index))).EnsureStatusAsync(HttpStatusCode.OK);

    private static async Task<NodeResponse> CompleteAsync(HttpClient client, UploadResponse upload)
    {
        var response = await client.PostAsync($"/api/v1/uploads/{upload.Id}/complete", null);
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return await response.ReadAsync<NodeResponse>();
    }

    private async Task<byte[]> ReadS3ObjectAsync(Guid nodeId)
    {
        string key = "", bucket = "";
        await factory.WithDbAsync(async db =>
        {
            var node = await db.Nodes.IgnoreQueryFilters().SingleAsync(n => n.Id == nodeId);
            var replica = await db.Replicas.IgnoreQueryFilters().SingleAsync(r => r.ObjectId == node.ObjectId);
            var account = await db.StorageAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == replica.StorageAccountId);
            key = replica.ProviderObjectId;
            bucket = System.Text.Json.JsonDocument.Parse(account.ConfigJson!).RootElement.GetProperty("bucket").GetString()!;
        });
        using var s3 = factory.S3.CreateClient();
        using var obj = await s3.GetObjectAsync(bucket, key);
        using var buffer = new MemoryStream();
        await obj.ResponseStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task S3_direct_upload_with_presigned_chunk_urls()
    {
        var alice = await factory.NewUserAsync();
        var account = await factory.ConnectS3Async(alice);
        var content = RandomContent();

        var upload = await StartAsync(alice.Client, "video.bin", content.Length);
        Assert.Equal(StorageProvider.S3, upload.Provider);
        Assert.Equal(2, upload.ChunkCount);
        Assert.Null(upload.DirectUploadUrl);

        var urls = await (await alice.Client.PostJsonAsync($"/api/v1/uploads/{upload.Id}/chunk-urls", new { chunks = new[] { 0, 1 } }))
            .ReadAsync<List<ChunkUrlResponse>>();
        using var direct = new HttpClient(); // what a browser would do: straight to the bucket, no API token
        foreach (var url in urls)
            Assert.True((await direct.PutAsync(url.Url, Chunk(content, upload, url.Index))).IsSuccessStatusCode);

        var progress = await alice.Client.GetJsonAsync<UploadProgressResponse>($"/api/v1/uploads/{upload.Id}");
        Assert.Equal([0, 1], progress.CompletedChunks);
        Assert.Equal(content.Length, progress.BytesReceived);

        var node = await CompleteAsync(alice.Client, upload);

        Assert.Equal("video.bin", node.Name);
        Assert.Equal(content.Length, node.SizeBytes);
        Assert.Equal(content, await ReadS3ObjectAsync(node.Id));
        var after = await factory.LoadAccountAsync(account.Id);
        Assert.Equal(content.Length, after.UsedBytes);
        Assert.Equal(0, after.ReservedBytes);
    }

    [Fact]
    public async Task S3_upload_proxied_through_the_api()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var folder = await (await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "Docs" })).ReadAsync<NodeResponse>();
        var content = RandomContent();

        var upload = await StartAsync(alice.Client, "report.pdf", content.Length, folder.Id);
        await PutChunkViaApiAsync(alice.Client, upload, content, 1); // S3 parts may arrive in any order
        await PutChunkViaApiAsync(alice.Client, upload, content, 0);
        var node = await CompleteAsync(alice.Client, upload);

        Assert.Equal(folder.Id, node.ParentId);
        Assert.Equal(content, await ReadS3ObjectAsync(node.Id));
    }

    [Fact]
    public async Task Google_upload_requires_chunks_in_order_and_stores_the_file()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectGoogleAsync(alice);
        var content = RandomContent();

        var upload = await StartAsync(alice.Client, "movie.mkv", content.Length);
        Assert.Equal(StorageProvider.GoogleDrive, upload.Provider);
        Assert.NotNull(upload.DirectUploadUrl);

        var presign = await alice.Client.PostJsonAsync($"/api/v1/uploads/{upload.Id}/chunk-urls", new { chunks = new[] { 0 } });
        var outOfOrder = await alice.Client.PutAsync($"/api/v1/uploads/{upload.Id}/chunks/1", Chunk(content, upload, 1));
        await PutChunkViaApiAsync(alice.Client, upload, content, 0);
        await PutChunkViaApiAsync(alice.Client, upload, content, 1);
        var node = await CompleteAsync(alice.Client, upload);

        Assert.Equal("presign_not_supported", await presign.ProblemCodeAsync());
        Assert.Equal("chunk_out_of_order", await outOfOrder.ProblemCodeAsync());
        var stored = Assert.Single(factory.Google.Files.Values, f => f.Content.Length == content.Length && f.Content.SequenceEqual(content));
        Assert.Equal(32, stored.Name.Length); // Drive file named by object id, never by the user's file name
        Assert.Equal("movie.mkv", node.Name);
    }

    [Fact]
    public async Task Protocol_violations_are_rejected()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var content = RandomContent();
        var upload = await StartAsync(alice.Client, "a.bin", content.Length);

        var wrongSize = await alice.Client.PutAsync($"/api/v1/uploads/{upload.Id}/chunks/0", new ByteArrayContent(new byte[10]));
        var badIndex = await alice.Client.PutAsync($"/api/v1/uploads/{upload.Id}/chunks/7", new ByteArrayContent(new byte[10]));
        await PutChunkViaApiAsync(alice.Client, upload, content, 0);
        var early = await alice.Client.PostAsync($"/api/v1/uploads/{upload.Id}/complete", null);
        var tooBig = await alice.Client.PostJsonAsync("/api/v1/uploads", new { fileName = "x", sizeBytes = 6L << 40 });

        Assert.Equal("invalid_chunk_size", await wrongSize.ProblemCodeAsync());
        Assert.Equal("invalid_chunk", await badIndex.ProblemCodeAsync());
        Assert.Equal("upload_incomplete", await early.ProblemCodeAsync());
        Assert.Equal("file_too_large", await tooBig.ProblemCodeAsync());
    }

    [Fact]
    public async Task Reservations_prevent_overcommitting_an_account_and_abort_releases_them()
    {
        var alice = await factory.NewUserAsync();
        var account = await factory.ConnectS3Async(alice, quotaBytes: 10 * MiB);

        var first = await StartAsync(alice.Client, "one.bin", 6 * MiB);
        var second = await alice.Client.PostJsonAsync("/api/v1/uploads", new { fileName = "two.bin", sizeBytes = 6 * MiB });
        Assert.Equal(6 * MiB, (await factory.LoadAccountAsync(account.Id)).ReservedBytes);

        await (await alice.Client.DeleteAsync($"/api/v1/uploads/{first.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        var retry = await alice.Client.PostJsonAsync("/api/v1/uploads", new { fileName = "two.bin", sizeBytes = 6 * MiB });

        await second.EnsureStatusAsync(HttpStatusCode.InsufficientStorage);
        Assert.Equal("insufficient_storage", await second.ProblemCodeAsync());
        await retry.EnsureStatusAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Routing_policy_decides_which_account_receives_uploads()
    {
        var alice = await factory.NewUserAsync();
        var small = await factory.ConnectS3Async(alice, quotaBytes: 100 * MiB, displayName: "small");
        var big = await factory.ConnectS3Async(alice, quotaBytes: 1000 * MiB, displayName: "big");
        await alice.Client.PatchJsonAsync($"/api/v1/storage-accounts/{small.Id}", new { priority = 0 });
        await alice.Client.PatchJsonAsync($"/api/v1/storage-accounts/{big.Id}", new { priority = 5 });

        async Task<Guid> TargetOfNextUploadAsync()
        {
            var upload = await StartAsync(alice.Client, $"f-{Guid.NewGuid():N}", MiB);
            await alice.Client.DeleteAsync($"/api/v1/uploads/{upload.Id}");
            return upload.StorageAccountId;
        }

        var mostAvailable = await TargetOfNextUploadAsync();
        await (await alice.Client.PutAsync("/api/v1/storage-accounts/routing-policy", JsonBody(new { policy = UploadRoutingPolicy.Priority }))).EnsureStatusAsync(HttpStatusCode.OK);
        var byPriority = await TargetOfNextUploadAsync();
        await alice.Client.PutAsync("/api/v1/storage-accounts/routing-policy", JsonBody(new { policy = UploadRoutingPolicy.RoundRobin }));
        var rotation = new[] { await TargetOfNextUploadAsync(), await TargetOfNextUploadAsync(), await TargetOfNextUploadAsync() };

        Assert.Equal(big.Id, mostAvailable);
        Assert.Equal(small.Id, byPriority);
        Assert.NotEqual(rotation[0], rotation[1]);
        Assert.Equal(rotation[0], rotation[2]);
    }

    [Fact]
    public async Task Completing_into_a_clashing_name_or_trashed_folder_still_publishes_the_file()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var folder = await (await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "Inbox" })).ReadAsync<NodeResponse>();
        var content = RandomContent(MiB);

        var first = await StartAsync(alice.Client, "photo.jpg", content.Length);
        var second = await StartAsync(alice.Client, "photo.jpg", content.Length);
        var intoFolder = await StartAsync(alice.Client, "late.txt", content.Length, folder.Id);
        foreach (var upload in new[] { first, second, intoFolder }) await PutChunkViaApiAsync(alice.Client, upload, content, 0);
        await alice.Client.PostAsync($"/api/v1/nodes/{folder.Id}/trash", null);

        Assert.Equal("photo.jpg", (await CompleteAsync(alice.Client, first)).Name);
        Assert.Equal("photo (1).jpg", (await CompleteAsync(alice.Client, second)).Name);
        Assert.Null((await CompleteAsync(alice.Client, intoFolder)).ParentId);
    }

    [Fact]
    public async Task Uploads_belong_to_their_starter_and_viewers_cannot_upload()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var carol = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var bobAsMember = await factory.JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Member);
        var carolAsViewer = await factory.JoinAndSwitchAsync(alice.Client, carol.Client, TenantRole.Viewer);
        var upload = await StartAsync(alice.Client, "mine.bin", MiB);

        var peek = await bobAsMember.GetAsync($"/api/v1/uploads/{upload.Id}");
        var hijack = await bobAsMember.PostAsync($"/api/v1/uploads/{upload.Id}/complete", null);
        var viewerStart = await carolAsViewer.PostJsonAsync("/api/v1/uploads", new { fileName = "x", sizeBytes = MiB });

        await peek.EnsureStatusAsync(HttpStatusCode.NotFound);
        await hijack.EnsureStatusAsync(HttpStatusCode.NotFound);
        Assert.Equal("insufficient_role", await viewerStart.ProblemCodeAsync());
    }

    [Fact]
    public async Task Expired_uploads_release_their_reservation_and_block_account_removal_until_then()
    {
        var alice = await factory.NewUserAsync();
        var account = await factory.ConnectS3Async(alice, quotaBytes: 100 * MiB);
        var upload = await StartAsync(alice.Client, "abandoned.bin", 5 * MiB);

        var removeDuringUpload = await alice.Client.DeleteAsync($"/api/v1/storage-accounts/{account.Id}");
        await factory.WithDbAsync(db => db.UploadSessions.IgnoreQueryFilters().Where(s => s.Id == upload.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<UploadService>().ExpireDueAsync(100, CancellationToken.None);

        Assert.Equal("storage_account_in_use", await removeDuringUpload.ProblemCodeAsync());
        Assert.Equal(UploadSessionStatus.Expired, (await alice.Client.GetJsonAsync<UploadProgressResponse>($"/api/v1/uploads/{upload.Id}")).Upload.Status);
        Assert.Equal(0, (await factory.LoadAccountAsync(account.Id)).ReservedBytes);
        await (await alice.Client.DeleteAsync($"/api/v1/storage-accounts/{account.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);
    }

    private static StringContent JsonBody(object body) =>
        new(System.Text.Json.JsonSerializer.Serialize(body, ApiClient.Json), System.Text.Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
}
