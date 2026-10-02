using System.Net;
using Amazon.S3;
using FreeSpace.Api.Files;
using FreeSpace.Api.StorageAccounts;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FreeSpace.Tests;

[Collection(ApiCollection.Name)]
public sealed class FileTreeTests(ApiFactory factory)
{
    private static async Task<NodeResponse> CreateFolderAsync(HttpClient client, string name, Guid? parentId = null)
    {
        var response = await client.PostJsonAsync("/api/v1/nodes/folders", new { name, parentId });
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return await response.ReadAsync<NodeResponse>();
    }

    /// <summary>Inserts a file directly (uploads arrive in a later phase), optionally with one replica.</summary>
    private async Task<Guid> SeedFileAsync(TestUser owner, string name, Guid? parentId, long size = 1024, Guid? storageAccountId = null, string? providerObjectId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var content = new StoredObject(owner.TenantId, size, "text/plain", StoredObjectStatus.Available, now);
        var node = Node.File(owner.TenantId, parentId, name, content, owner.UserId, now);
        db.AddRange(content, node);
        if (storageAccountId is { } accountId)
            db.Add(new Replica(owner.TenantId, content.Id, accountId, providerObjectId!, ReplicaStatus.Available, now));
        await db.SaveChangesAsync();
        return node.Id;
    }

    private async Task<int> PurgeAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ReplicaPurger>().PurgeBatchAsync(100, CancellationToken.None);
    }

    private static Task<NodePageResponse> ListAsync(HttpClient client, Guid? parentId = null, string? cursor = null, int? limit = null)
    {
        var query = new List<string>();
        if (parentId is not null) query.Add($"parentId={parentId}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (limit is not null) query.Add($"limit={limit}");
        return client.GetJsonAsync<NodePageResponse>("/api/v1/nodes" + (query.Count > 0 ? "?" + string.Join('&', query) : ""));
    }

    [Fact]
    public async Task Listing_puts_folders_first_sorted_by_name_and_paginates()
    {
        var alice = await factory.NewUserAsync();
        await CreateFolderAsync(alice.Client, "beta");
        await CreateFolderAsync(alice.Client, "Alpha");
        await SeedFileAsync(alice, "c.txt", null);
        await SeedFileAsync(alice, "a.txt", null);

        var first = await ListAsync(alice.Client, limit: 3);
        var second = await ListAsync(alice.Client, cursor: first.NextCursor, limit: 3);

        Assert.Equal(["Alpha", "beta", "a.txt"], first.Items.Select(i => i.Name));
        Assert.NotNull(first.NextCursor);
        Assert.Equal(["c.txt"], second.Items.Select(i => i.Name));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Sibling_names_are_unique_case_insensitively_but_only_within_a_folder()
    {
        var alice = await factory.NewUserAsync();
        var docs = await CreateFolderAsync(alice.Client, "Docs");

        var clash = await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "docs" });
        await CreateFolderAsync(alice.Client, "docs", parentId: docs.Id); // a different folder is fine
        var invalid = await alice.Client.PostJsonAsync("/api/v1/nodes/folders", new { name = "a/b" });

        await clash.EnsureStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal("name_conflict", await clash.ProblemCodeAsync());
        Assert.Equal("invalid_name", await invalid.ProblemCodeAsync());
    }

    [Fact]
    public async Task Move_updates_breadcrumbs_and_rejects_cycles_and_clashes()
    {
        var alice = await factory.NewUserAsync();
        var a = await CreateFolderAsync(alice.Client, "A");
        var b = await CreateFolderAsync(alice.Client, "B");
        await CreateFolderAsync(alice.Client, "B", parentId: a.Id);

        var clash = await alice.Client.PostJsonAsync($"/api/v1/nodes/{b.Id}/move", new { parentId = a.Id });
        await (await alice.Client.PatchJsonAsync($"/api/v1/nodes/{b.Id}", new { name = "C" })).EnsureStatusAsync(HttpStatusCode.OK);
        await (await alice.Client.PostJsonAsync($"/api/v1/nodes/{b.Id}/move", new { parentId = a.Id })).EnsureStatusAsync(HttpStatusCode.OK);
        var cycle = await alice.Client.PostJsonAsync($"/api/v1/nodes/{a.Id}/move", new { parentId = b.Id });
        var details = await alice.Client.GetJsonAsync<NodeDetailsResponse>($"/api/v1/nodes/{b.Id}");

        Assert.Equal("name_conflict", await clash.ProblemCodeAsync());
        Assert.Equal("invalid_move", await cycle.ProblemCodeAsync());
        Assert.Equal(["A", "C"], details.Path.Select(p => p.Name));
    }

    [Fact]
    public async Task Trashing_a_folder_hides_its_subtree_and_restore_brings_it_back()
    {
        var alice = await factory.NewUserAsync();
        var a = await CreateFolderAsync(alice.Client, "A");
        var b = await CreateFolderAsync(alice.Client, "B", parentId: a.Id);
        var file = await SeedFileAsync(alice, "notes.txt", b.Id);

        await (await alice.Client.PostAsync($"/api/v1/nodes/{a.Id}/trash", null)).EnsureStatusAsync(HttpStatusCode.NoContent);

        Assert.Empty((await ListAsync(alice.Client)).Items);
        await (await alice.Client.GetAsync($"/api/v1/nodes/{file}")).EnsureStatusAsync(HttpStatusCode.NotFound);
        var trash = Assert.Single(await alice.Client.GetJsonAsync<List<TrashItemResponse>>("/api/v1/trash"));
        Assert.Equal(a.Id, trash.Node.Id);
        Assert.Equal(3, trash.ItemCount);

        await (await alice.Client.PostAsync($"/api/v1/trash/{a.Id}/restore", null)).EnsureStatusAsync(HttpStatusCode.OK);

        var path = await alice.Client.GetJsonAsync<NodeDetailsResponse>($"/api/v1/nodes/{file}");
        Assert.Equal(["A", "B", "notes.txt"], path.Path.Select(p => p.Name));
        Assert.Empty(await alice.Client.GetJsonAsync<List<TrashItemResponse>>("/api/v1/trash"));
    }

    [Fact]
    public async Task Restore_renames_on_clash_and_falls_back_to_root_when_parent_is_trashed()
    {
        var alice = await factory.NewUserAsync();
        var x = await CreateFolderAsync(alice.Client, "X");
        await (await alice.Client.PostAsync($"/api/v1/nodes/{x.Id}/trash", null)).EnsureStatusAsync(HttpStatusCode.NoContent);
        await CreateFolderAsync(alice.Client, "X");

        var parent = await CreateFolderAsync(alice.Client, "Parent");
        var child = await CreateFolderAsync(alice.Client, "Child", parentId: parent.Id);
        await (await alice.Client.PostAsync($"/api/v1/nodes/{child.Id}/trash", null)).EnsureStatusAsync(HttpStatusCode.NoContent);
        await (await alice.Client.PostAsync($"/api/v1/nodes/{parent.Id}/trash", null)).EnsureStatusAsync(HttpStatusCode.NoContent);

        var restoredX = await (await alice.Client.PostAsync($"/api/v1/trash/{x.Id}/restore", null)).ReadAsync<NodeResponse>();
        var restoredChild = await (await alice.Client.PostAsync($"/api/v1/trash/{child.Id}/restore", null)).ReadAsync<NodeResponse>();

        Assert.Equal("X (1)", restoredX.Name);
        Assert.Null(restoredChild.ParentId);
    }

    [Fact]
    public async Task Permanent_delete_purges_s3_objects_and_frees_the_account()
    {
        var alice = await factory.NewUserAsync();
        var bucket = await factory.S3.CreateBucketAsync();
        var connect = await alice.Client.PostJsonAsync("/api/v1/storage-accounts/s3", new
        {
            displayName = "S3", endpoint = factory.S3.GetConnectionString(), region = "us-east-1", bucket,
            accessKeyId = S3TestServer.AccessKey, secretAccessKey = S3TestServer.SecretKey,
        });
        var account = await connect.ReadAsync<StorageAccountResponse>();
        const string key = "freespace/objects/file-1";
        using (var s3 = factory.S3.CreateClient())
            await s3.PutObjectAsync(new() { BucketName = bucket, Key = key, ContentBody = "hello" });

        var folder = await CreateFolderAsync(alice.Client, "Stuff");
        await SeedFileAsync(alice, "hello.txt", folder.Id, size: 5, account.Id, key);

        var removeWhileInUse = await alice.Client.DeleteAsync($"/api/v1/storage-accounts/{account.Id}");
        await (await alice.Client.PostAsync($"/api/v1/nodes/{folder.Id}/trash", null)).EnsureStatusAsync(HttpStatusCode.NoContent);
        await (await alice.Client.DeleteAsync($"/api/v1/trash/{folder.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(1, await PurgeAsync());

        Assert.Equal("storage_account_in_use", await removeWhileInUse.ProblemCodeAsync());
        using (var s3 = factory.S3.CreateClient())
            await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.GetObjectMetadataAsync(bucket, key));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Replicas.IgnoreQueryFilters().AnyAsync(r => r.StorageAccountId == account.Id));
            Assert.False(await db.StoredObjects.IgnoreQueryFilters().AnyAsync(o => o.TenantId == alice.TenantId));
            Assert.False(await db.Nodes.IgnoreQueryFilters().AnyAsync(n => n.TenantId == alice.TenantId));
        }
        await (await alice.Client.DeleteAsync($"/api/v1/storage-accounts/{account.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Emptying_trash_purges_google_drive_files()
    {
        var alice = await factory.NewUserAsync();
        await (await alice.Client.PostAsync("/api/v1/storage-accounts/google/authorize", null)).EnsureStatusAsync(HttpStatusCode.OK);
        var code = factory.Google.Consent($"sub-{Guid.NewGuid():N}", "a@gmail.com");
        var callbackClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await (await callbackClient.GetAsync($"/api/v1/storage-accounts/google/callback?code={code}&state={Uri.EscapeDataString(factory.Google.LastState!)}"))
            .EnsureStatusAsync(HttpStatusCode.OK);
        var account = Assert.Single(await alice.Client.GetJsonAsync<List<StorageAccountResponse>>("/api/v1/storage-accounts"));
        var driveFileId = $"drive-{Guid.NewGuid():N}";
        var file = await SeedFileAsync(alice, "movie.mkv", null, size: 2048, account.Id, driveFileId);

        await (await alice.Client.PostAsync($"/api/v1/nodes/{file}/trash", null)).EnsureStatusAsync(HttpStatusCode.NoContent);
        await (await alice.Client.DeleteAsync("/api/v1/trash")).EnsureStatusAsync(HttpStatusCode.NoContent);
        await PurgeAsync();

        Assert.True(factory.Google.DeletedFiles.ContainsKey(driveFileId));
    }

    [Fact]
    public async Task Search_matches_substrings_literally()
    {
        var alice = await factory.NewUserAsync();
        await CreateFolderAsync(alice.Client, "Report 100%_final");
        await CreateFolderAsync(alice.Client, "Report 1000 final");
        await SeedFileAsync(alice, "report.pdf", null);

        var literal = await alice.Client.GetJsonAsync<List<NodeResponse>>($"/api/v1/nodes/search?q={Uri.EscapeDataString("100%_")}");
        var filesOnly = await alice.Client.GetJsonAsync<List<NodeResponse>>("/api/v1/nodes/search?q=REPORT&kind=file");

        Assert.Equal(["Report 100%_final"], literal.Select(n => n.Name));
        Assert.Equal(["report.pdf"], filesOnly.Select(n => n.Name));
    }

    [Fact]
    public async Task Viewers_read_but_cannot_change_and_members_cannot_empty_trash()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var carol = await factory.NewUserAsync();
        var folder = await CreateFolderAsync(alice.Client, "Shared");
        var bobAsViewer = await factory.JoinAndSwitchAsync(alice.Client, bob.Client, TenantRole.Viewer);
        var carolAsMember = await factory.JoinAndSwitchAsync(alice.Client, carol.Client, TenantRole.Member);

        Assert.Single((await ListAsync(bobAsViewer)).Items);
        var create = await bobAsViewer.PostJsonAsync("/api/v1/nodes/folders", new { name = "Mine" });
        var trash = await bobAsViewer.PostAsync($"/api/v1/nodes/{folder.Id}/trash", null);
        await CreateFolderAsync(carolAsMember, "Carol's");
        var empty = await carolAsMember.DeleteAsync("/api/v1/trash");

        Assert.Equal("insufficient_role", await create.ProblemCodeAsync());
        Assert.Equal("insufficient_role", await trash.ProblemCodeAsync());
        Assert.Equal("insufficient_role", await empty.ProblemCodeAsync());
    }

    [Fact]
    public async Task Other_tenants_cannot_see_or_target_nodes()
    {
        var alice = await factory.NewUserAsync();
        var mallory = await factory.NewUserAsync();
        var secret = await CreateFolderAsync(alice.Client, "Secret");
        var mine = await CreateFolderAsync(mallory.Client, "Mine");

        await (await mallory.Client.GetAsync($"/api/v1/nodes/{secret.Id}")).EnsureStatusAsync(HttpStatusCode.NotFound);
        await (await mallory.Client.GetAsync($"/api/v1/nodes?parentId={secret.Id}")).EnsureStatusAsync(HttpStatusCode.NotFound);
        var moveIn = await mallory.Client.PostJsonAsync($"/api/v1/nodes/{mine.Id}/move", new { parentId = secret.Id });
        var trashIt = await mallory.Client.PostAsync($"/api/v1/nodes/{secret.Id}/trash", null);

        Assert.Equal("folder_not_found", await moveIn.ProblemCodeAsync());
        await trashIt.EnsureStatusAsync(HttpStatusCode.NotFound);
        Assert.Empty(await mallory.Client.GetJsonAsync<List<NodeResponse>>("/api/v1/nodes/search?q=Secret"));
    }
}
