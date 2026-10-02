using System.IO.Compression;
using System.Text;
using FreeSpace.Api.Configurations;
using FreeSpace.Domain.Files;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.Files;

/// <summary>
/// Streams a zip of files and folders (recursively, keeping the folder structure) straight from the
/// providers to the client, one entry at a time: nothing is staged on disk or held in memory.
/// </summary>
public sealed class ZipResult(IReadOnlyList<Node> roots, Guid tenantId, string zipName) : IActionResult
{
    private static readonly string[] AllTenants = [AppDbContext.TenantFilter];

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var services = http.RequestServices;
        var limits = services.GetRequiredService<IOptions<StorageOptions>>().Value;

        var entries = await PlanAsync(services.GetRequiredService<AppDbContext>(), services.GetRequiredService<FileTree>(), ct);
        if (entries.Count > limits.MaxZipEntries)
        {
            await ContentProblems.WriteAsync(context, StatusCodes.Status400BadRequest, "zip_too_large", $"A zip can hold at most {limits.MaxZipEntries} items.");
            return;
        }
        if (entries.Sum(e => e.File?.SizeBytes ?? 0) > limits.MaxZipBytes)
        {
            await ContentProblems.WriteAsync(context, StatusCodes.Status400BadRequest, "zip_too_large", $"A zip can hold at most {limits.MaxZipBytes} bytes.");
            return;
        }

        var response = http.Response;
        response.ContentType = "application/zip";
        response.Headers.ContentDisposition = ContentDispositions.Attachment(zipName);
        ContentProblems.ApplyContentSecurityHeaders(response);

        var reader = services.GetRequiredService<ContentReader>();
        try
        {
            await using var output = new AsyncOnlyWriteStream(response.Body);
            await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, ct);
            foreach (var entry in entries)
            {
                if (entry.File is not { } file)
                {
                    await using (await zip.CreateEntry(entry.Path + "/").OpenAsync(ct)) { } // keeps empty folders
                    continue;
                }

                var zipEntry = zip.CreateEntry(entry.Path, Compressible(file.MimeType) ? CompressionLevel.Fastest : CompressionLevel.NoCompression);
                zipEntry.LastWriteTime = file.UpdatedAt;
                await using var target = await zipEntry.OpenAsync(ct);
                await using var source = await reader.OpenAsync(tenantId, file, range: null, ct);
                await source.CopyToAsync(target, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The zip is half written; abort so the client cannot mistake it for a complete archive.
            services.GetRequiredService<ILogger<ZipResult>>().LogWarning(e, "Streaming zip failed midway");
            http.Abort();
        }
    }

    private sealed record ZipEntryPlan(string Path, Node? File);

    /// <summary>Expands folders into their live subtree and assigns unique archive paths.</summary>
    private async Task<List<ZipEntryPlan>> PlanAsync(AppDbContext db, FileTree tree, CancellationToken ct)
    {
        var plan = new List<ZipEntryPlan>();
        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var rootPath = UniqueName(rootNames, root.Name);
            if (!root.IsFolder)
            {
                plan.Add(new ZipEntryPlan(rootPath, root));
                continue;
            }

            var ids = await tree.SubtreeIdsAsync(tenantId, [root.Id], liveOnly: true, ct);
            var nodes = await db.Nodes.IgnoreQueryFilters(AllTenants).Where(n => n.TenantId == tenantId && ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
            var paths = new Dictionary<Guid, string> { [root.Id] = rootPath };

            string PathOf(Node node) =>
                paths.TryGetValue(node.Id, out var known) ? known : paths[node.Id] = $"{PathOf(nodes[node.ParentId!.Value])}/{node.Name}";

            foreach (var node in nodes.Values.OrderBy(n => n.IsFolder ? 0 : 1))
                plan.Add(new ZipEntryPlan(PathOf(node), node.IsFolder ? null : node));
        }
        return plan.OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
    }

    private static string UniqueName(HashSet<string> taken, string name)
    {
        var candidate = name;
        for (var i = 1; !taken.Add(candidate); i++) candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
        return candidate;
    }

    /// <summary>
    /// ZipArchive still writes some headers synchronously (e.g. when an entry stream is disposed), and
    /// ASP.NET Core forbids synchronous I/O on the response. Those small sync writes are buffered and
    /// sent ahead of the next asynchronous write; file data keeps streaming straight through.
    /// </summary>
    private sealed class AsyncOnlyWriteStream(Stream inner) : Stream
    {
        private readonly MemoryStream _pending = new();
        private long _position;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _pending.Write(buffer);
            _position += buffer.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await FlushPendingAsync(ct);
            await inner.WriteAsync(buffer, ct);
            _position += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async Task FlushAsync(CancellationToken ct)
        {
            await FlushPendingAsync(ct);
            await inner.FlushAsync(ct);
        }

        public override void Flush() { } // nothing can be pushed synchronously; FlushAsync/DisposeAsync do it

        public override async ValueTask DisposeAsync()
        {
            await FlushPendingAsync(CancellationToken.None);
            await base.DisposeAsync();
        }

        private async Task FlushPendingAsync(CancellationToken ct)
        {
            if (_pending.Length == 0) return;
            await inner.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
            _pending.SetLength(0);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Media and archives are already compressed; recompressing them only burns CPU.</summary>
    private static bool Compressible(string? mimeType) =>
        mimeType is not null && (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                                 || mimeType.Contains("json", StringComparison.OrdinalIgnoreCase)
                                 || mimeType.Contains("xml", StringComparison.OrdinalIgnoreCase));
}
