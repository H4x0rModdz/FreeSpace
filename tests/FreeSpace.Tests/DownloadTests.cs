using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using FreeSpace.Api.Files;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Tests;

[Collection(ApiCollection.Name)]
public sealed class DownloadTests(ApiFactory factory)
{
    private static byte[] RandomContent(int size = 1 << 20, int seed = 7)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task<HttpResponseMessage> GetRangeAsync(HttpClient client, string url, string range)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Range", range);
        return await client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values)
        : response.Content.Headers.TryGetValues(name, out var contentValues) ? string.Join(",", contentValues) : "";

    [Fact]
    public async Task S3_download_supports_full_content_and_ranges()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var content = RandomContent();
        var file = await alice.Client.UploadFileAsync("data.bin", content);
        var url = $"/api/v1/nodes/{file.Id}/content";

        var full = await alice.Client.GetAsync(url);
        var middle = await GetRangeAsync(alice.Client, url, "bytes=100-199");
        var suffix = await GetRangeAsync(alice.Client, url, "bytes=-10");
        var openEnded = await GetRangeAsync(alice.Client, url, $"bytes={content.Length - 6}-");
        var beyond = await GetRangeAsync(alice.Client, url, $"bytes={content.Length + 5}-");

        await full.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(content, await full.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", Header(full, "Accept-Ranges"));
        Assert.Equal("nosniff", Header(full, "X-Content-Type-Options"));
        Assert.Contains("sandbox", Header(full, "Content-Security-Policy"));
        Assert.StartsWith("attachment;", Header(full, "Content-Disposition"));

        await middle.EnsureStatusAsync(HttpStatusCode.PartialContent);
        Assert.Equal($"bytes 100-199/{content.Length}", Header(middle, "Content-Range"));
        Assert.Equal(content[100..200], await middle.Content.ReadAsByteArrayAsync());
        Assert.Equal(content[^10..], await suffix.Content.ReadAsByteArrayAsync());
        Assert.Equal(content[^6..], await openEnded.Content.ReadAsByteArrayAsync());
        await beyond.EnsureStatusAsync(HttpStatusCode.RequestedRangeNotSatisfiable);
        Assert.Equal($"bytes */{content.Length}", Header(beyond, "Content-Range"));
    }

    [Fact]
    public async Task Google_download_streams_through_the_api_with_ranges()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectGoogleAsync(alice);
        var content = RandomContent(seed: 11);
        var file = await alice.Client.UploadFileAsync("clip.mp4", content, mimeType: "video/mp4");

        var range = await GetRangeAsync(alice.Client, $"/api/v1/nodes/{file.Id}/content?inline=true", "bytes=1000-1999");

        await range.EnsureStatusAsync(HttpStatusCode.PartialContent);
        Assert.Equal(content[1000..2000], await range.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("inline;", Header(range, "Content-Disposition"));
        Assert.Equal("video/mp4", range.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Only_safe_types_render_inline_and_names_keep_their_accents()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var html = await alice.Client.UploadFileAsync("page.html", "<script>alert(1)</script>"u8.ToArray(), mimeType: "text/html");
        var image = await alice.Client.UploadFileAsync("relatório ação.png", RandomContent(1024), mimeType: "image/png");

        var htmlResponse = await alice.Client.GetAsync($"/api/v1/nodes/{html.Id}/content?inline=true");
        var imageResponse = await alice.Client.GetAsync($"/api/v1/nodes/{image.Id}/content?inline=true");

        Assert.StartsWith("attachment;", Header(htmlResponse, "Content-Disposition"));
        Assert.Contains("sandbox", Header(htmlResponse, "Content-Security-Policy"));
        Assert.StartsWith("inline;", Header(imageResponse, "Content-Disposition"));
        Assert.Contains("filename*=UTF-8''relat%C3%B3rio%20a%C3%A7%C3%A3o.png", Header(imageResponse, "Content-Disposition"));
    }

    [Fact]
    public async Task Content_links_work_without_a_token_and_stop_when_the_file_is_trashed()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectGoogleAsync(alice);
        var content = RandomContent(4096, seed: 3);
        var file = await alice.Client.UploadFileAsync("song.mp3", content, mimeType: "audio/mpeg");
        var anonymous = factory.CreateClient();

        var link = await (await alice.Client.PostAsync($"/api/v1/nodes/{file.Id}/content-link", null)).ReadAsync<ContentLinkResponse>();
        var downloaded = await anonymous.GetAsync(link.Url);
        var tampered = await anonymous.GetAsync(link.Url[..^4] + "AAAA");
        await alice.Client.PostAsync($"/api/v1/nodes/{file.Id}/trash", null);
        var afterTrash = await anonymous.GetAsync(link.Url);

        Assert.False(link.Direct); // Drive has no presigned URLs
        Assert.Equal(content, await downloaded.Content.ReadAsByteArrayAsync());
        Assert.Equal("link_invalid", await tampered.ProblemCodeAsync());
        Assert.Equal("link_invalid", await afterTrash.ProblemCodeAsync());
    }

    [Fact]
    public async Task S3_content_links_point_straight_at_the_bucket()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var content = RandomContent(2048, seed: 5);
        var file = await alice.Client.UploadFileAsync("doc.pdf", content, mimeType: "application/pdf");

        var direct = await (await alice.Client.PostAsync($"/api/v1/nodes/{file.Id}/content-link", null)).ReadAsync<ContentLinkResponse>();
        var forPreview = await (await alice.Client.PostAsync($"/api/v1/nodes/{file.Id}/content-link?inline=true", null)).ReadAsync<ContentLinkResponse>();
        using var bucketClient = new HttpClient();
        var fromBucket = await bucketClient.GetAsync(direct.Url);

        Assert.True(direct.Direct);
        Assert.StartsWith(factory.S3.GetConnectionString(), direct.Url);
        Assert.Equal(content, await fromBucket.Content.ReadAsByteArrayAsync());
        Assert.False(forPreview.Direct);
    }

    [Fact]
    public async Task Zip_streams_folders_recursively_with_their_structure()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var projects = await (await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "Projects" })).ReadAsync<NodeResponse>();
        var docs = await (await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "Docs", parentId = projects.Id })).ReadAsync<NodeResponse>();
        await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "Empty", parentId = projects.Id });
        var readme = "hello"u8.ToArray();
        var spec = RandomContent(3000, seed: 9);
        await alice.Client.UploadFileAsync("readme.txt", readme, projects.Id, "text/plain");
        await alice.Client.UploadFileAsync("spec.bin", spec, docs.Id);
        var loose = await alice.Client.UploadFileAsync("loose.bin", RandomContent(10, seed: 1));

        var response = await alice.Client.PostJsonAsync("/api/v1/nodes/zip", new { nodeIds = new[] { projects.Id, loose.Id }, name = "bundle" });

        await response.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Contains("bundle.zip", Header(response, "Content-Disposition"));
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(
            ["Projects/", "Projects/Docs/", "Projects/Docs/spec.bin", "Projects/Empty/", "Projects/readme.txt", "loose.bin"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(spec, await ReadEntryAsync(zip, "Projects/Docs/spec.bin"));
        Assert.Equal(readme, await ReadEntryAsync(zip, "Projects/readme.txt"));
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchive zip, string path)
    {
        await using var stream = zip.GetEntry(path)!.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task Public_folder_share_exposes_only_its_subtree()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var shared = await (await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "Photos" })).ReadAsync<NodeResponse>();
        var trip = await (await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "Trip", parentId = shared.Id })).ReadAsync<NodeResponse>();
        var photo = RandomContent(5000, seed: 21);
        var photoNode = await alice.Client.UploadFileAsync("beach.jpg", photo, trip.Id, "image/jpeg");
        var secret = await alice.Client.UploadFileAsync("secret.txt", "nope"u8.ToArray());

        var created = await (await alice.Client.PostJsonAsync($"/api/v1/nodes/{shared.Id}/shares", new { })).ReadAsync<CreatedShareResponse>();
        var anonymous = factory.CreateClient();
        var basePath = $"/api/v1/public/shares/{created.Token}";

        var info = await anonymous.GetJsonAsync<PublicShareResponse>(basePath);
        var top = await anonymous.GetJsonAsync<List<PublicNodeResponse>>($"{basePath}/nodes");
        var inTrip = await anonymous.GetJsonAsync<List<PublicNodeResponse>>($"{basePath}/nodes?parentId={trip.Id}");
        var download = await anonymous.GetAsync($"{basePath}/content?nodeId={photoNode.Id}");
        var outside = await anonymous.GetAsync($"{basePath}/content?nodeId={secret.Id}");
        var zip = await anonymous.GetAsync($"{basePath}/zip");

        Assert.Equal("Photos", info.Name);
        Assert.Equal(["Trip"], top.Select(n => n.Name));
        Assert.Equal(["beach.jpg"], inTrip.Select(n => n.Name));
        Assert.Equal(photo, await download.Content.ReadAsByteArrayAsync());
        await outside.EnsureStatusAsync(HttpStatusCode.NotFound);
        using var archive = new ZipArchive(new MemoryStream(await zip.Content.ReadAsByteArrayAsync()));
        Assert.Contains(archive.Entries, e => e.FullName == "Photos/Trip/beach.jpg");
    }

    [Fact]
    public async Task Share_links_die_on_revoke_expiry_and_trash()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var file = await alice.Client.UploadFileAsync("contract.pdf", RandomContent(100), mimeType: "application/pdf");
        async Task<string> ShareAsync(object body) =>
            (await (await alice.Client.PostJsonAsync($"/api/v1/nodes/{file.Id}/shares", body)).ReadAsync<CreatedShareResponse>()).Token;
        var anonymous = factory.CreateClient();

        var revoked = await ShareAsync(new { });
        var expiring = await ShareAsync(new { expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var survivor = await ShareAsync(new { });
        var pastExpiry = await alice.Client.PostJsonAsync($"/api/v1/nodes/{file.Id}/shares", new { expiresAt = DateTimeOffset.UtcNow.AddHours(-1) });

        var shares = await alice.Client.GetJsonAsync<List<ShareResponse>>("/api/v1/shares");
        await alice.Client.DeleteAsync($"/api/v1/shares/{shares.Last().Id}"); // the first one created
        await factory.WithDbAsync(db => db.Shares.IgnoreQueryFilters().Where(s => s.NodeId == file.Id && s.ExpiresAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

        Assert.Equal("invalid_expiry", await pastExpiry.ProblemCodeAsync());
        Assert.Equal("share_not_found", await (await anonymous.GetAsync($"/api/v1/public/shares/{revoked}")).ProblemCodeAsync());
        Assert.Equal("share_not_found", await (await anonymous.GetAsync($"/api/v1/public/shares/{expiring}")).ProblemCodeAsync());
        await (await anonymous.GetAsync($"/api/v1/public/shares/{survivor}")).EnsureStatusAsync(HttpStatusCode.OK);

        await alice.Client.PostAsync($"/api/v1/nodes/{file.Id}/trash", null);
        await (await anonymous.GetAsync($"/api/v1/public/shares/{survivor}")).EnsureStatusAsync(HttpStatusCode.NotFound);
        await alice.Client.PostAsync($"/api/v1/trash/{file.Id}/restore", null);
        await (await anonymous.GetAsync($"/api/v1/public/shares/{survivor}/content")).EnsureStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Share_permissions_follow_roles()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var carol = await factory.NewUserAsync();
        await factory.ConnectS3Async(alice);
        var file = await alice.Client.UploadFileAsync("x.bin", RandomContent(10));
        var bobAsViewer = await factory.JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Viewer);
        var carolAsMember = await factory.JoinAndSwitchAsync(alice.Client, carol.Client, TenantRole.Member);
        var aliceShare = await (await alice.Client.PostJsonAsync($"/api/v1/nodes/{file.Id}/shares", new { })).ReadAsync<CreatedShareResponse>();

        var viewerCreate = await bobAsViewer.PostJsonAsync($"/api/v1/nodes/{file.Id}/shares", new { });
        var memberRevokesOthers = await carolAsMember.DeleteAsync($"/api/v1/shares/{aliceShare.Id}");
        var viewerDownload = await bobAsViewer.GetAsync($"/api/v1/nodes/{file.Id}/content");

        Assert.Equal("insufficient_role", await viewerCreate.ProblemCodeAsync());
        Assert.Equal("insufficient_role", await memberRevokesOthers.ProblemCodeAsync());
        await viewerDownload.EnsureStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_bytes_at_the_provider_are_reported()
    {
        var alice = await factory.NewUserAsync();
        await factory.ConnectGoogleAsync(alice);
        var file = await alice.Client.UploadFileAsync("gone.bin", RandomContent(64));
        foreach (var key in factory.Google.Files.Keys.ToList())
            if (factory.Google.Files[key].Content.Length == 64) factory.Google.Files.TryRemove(key, out _);

        var response = await alice.Client.GetAsync($"/api/v1/nodes/{file.Id}/content");

        await response.EnsureStatusAsync(HttpStatusCode.BadGateway);
        Assert.Equal("content_missing", await response.ProblemCodeAsync());
    }
}
