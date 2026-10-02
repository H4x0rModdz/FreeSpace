using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using FreeSpace.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FreeSpace.Infrastructure.Files;

/// <param name="BrowserOrigin">Origin of the web app, so Drive accepts chunks sent directly from the browser.</param>
public sealed record StartUploadCommand(Guid TenantId, Guid UserId, Guid? ParentId, string FileName, string MimeType, long SizeBytes, string? BrowserOrigin, TimeSpan Lifetime);

public sealed record StartedUpload(UploadSession Session, StorageAccount Account, string? DirectUrl);

/// <summary>
/// Upload lifecycle: reserve space → open the provider upload → receive chunks (direct or proxied)
/// → complete (replica + available object + file node) or abort/expire (release everything).
/// Sessions are loaded by id across the tenant filter so background expiry can use the same code;
/// callers authorize access to the session first.
/// </summary>
public sealed class UploadService(
    AppDbContext db, StorageAllocator allocator, StorageAccounting accounting, StorageProviderRegistry providers,
    ISecretProtector protector, FileTree tree, TimeProvider clock, ILogger<UploadService> logger)
{
    /// <summary>S3 parts must be at least 5 MiB; Drive chunks multiples of 256 KiB. 8 MiB satisfies both.</summary>
    public const long MinChunkSize = 8L << 20;
    private const long ChunkGranularity = 256L << 10;
    /// <summary>S3 allows at most 10,000 parts per multipart upload.</summary>
    private const int MaxChunks = 10_000;

    private static readonly string[] AllTenants = [AppDbContext.TenantFilter];

    public static long ChunkSizeFor(long sizeBytes)
    {
        var size = Math.Max(MinChunkSize, (sizeBytes + MaxChunks - 1) / MaxChunks);
        return (size + ChunkGranularity - 1) / ChunkGranularity * ChunkGranularity;
    }

    public async Task<StartedUpload> StartAsync(StartUploadCommand command, CancellationToken ct)
    {
        var account = await allocator.ReserveAsync(command.TenantId, command.SizeBytes, ct)
                      ?? throw new UploadProtocolException("insufficient_storage", "No connected storage account has enough free space for this file.");

        var now = clock.GetUtcNow();
        var chunkSize = ChunkSizeFor(command.SizeBytes);
        var content = new StoredObject(command.TenantId, command.SizeBytes, command.MimeType, StoredObjectStatus.Pending, now);
        var session = new UploadSession(command.TenantId, command.UserId, command.ParentId, command.FileName, command.MimeType,
            command.SizeBytes, chunkSize, content.Id, account.Id, now + command.Lifetime, now);

        UploadStart start;
        try
        {
            start = await providers.Get(account.Provider)
                .BeginUploadAsync(account, new UploadSpec(command.TenantId, content.Id, command.SizeBytes, command.MimeType, command.BrowserOrigin), chunkSize, ct);
        }
        catch (Exception e) when (e is StorageAuthException or StorageConnectionException)
        {
            await accounting.ReleaseAsync(account.Id, command.SizeBytes, CancellationToken.None);
            if (e is StorageAuthException)
            {
                account.MarkNeedsReauth(e.Message, now); // keep the allocator away from it until reconnected
                await db.SaveChangesAsync(CancellationToken.None);
            }
            throw;
        }

        session.SetProviderHandle(start.Upload.ObjectKey, protector.Protect(start.Upload.Handle, session.Id.ToString()));
        db.AddRange(content, session);
        await db.SaveChangesAsync(ct); // also persists provider config learned while opening (Drive folder id)
        return new StartedUpload(session, account, start.DirectUrl);
    }

    public Task<UploadSession?> FindAsync(Guid sessionId, CancellationToken ct) =>
        db.UploadSessions.IgnoreQueryFilters(AllTenants).FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public async Task<UploadProgress> ProgressAsync(UploadSession session, CancellationToken ct)
    {
        var (provider, account) = await ProviderForAsync(session, ct);
        return await provider.GetUploadProgressAsync(account, Handle(session), ct);
    }

    public async Task<UploadProgress> UploadChunkAsync(UploadSession session, int index, long contentLength, Stream content, CancellationToken ct)
    {
        EnsurePending(session);
        if (index < 0 || index >= session.ChunkCount)
            throw new UploadProtocolException("invalid_chunk", $"Chunk index must be between 0 and {session.ChunkCount - 1}.");
        var (offset, length) = session.ChunkRange(index);
        if (contentLength != length)
            throw new UploadProtocolException("invalid_chunk_size", $"Chunk {index} must be exactly {length} bytes.");

        var (provider, account) = await ProviderForAsync(session, ct);
        return await provider.UploadChunkAsync(account, Handle(session), index, offset, length, content, ct);
    }

    public async Task<IReadOnlyList<PresignedChunk>> PresignAsync(UploadSession session, IReadOnlyList<int> indexes, CancellationToken ct)
    {
        EnsurePending(session);
        if (indexes.Any(i => i < 0 || i >= session.ChunkCount))
            throw new UploadProtocolException("invalid_chunk", $"Chunk indexes must be between 0 and {session.ChunkCount - 1}.");
        var (provider, account) = await ProviderForAsync(session, ct);
        return await provider.PresignChunksAsync(account, Handle(session), indexes, ct);
    }

    /// <summary>Finalizes at the provider and publishes the file. Name clashes get a " (n)" suffix; a vanished folder means the root.</summary>
    public async Task<Node> CompleteAsync(UploadSession session, CancellationToken ct)
    {
        EnsurePending(session);
        var (provider, account) = await ProviderForAsync(session, ct);
        var completed = await provider.CompleteUploadAsync(account, Handle(session), ct);
        if (completed.SizeBytes != session.SizeBytes)
        {
            await AbortAsync(session, UploadSessionStatus.Aborted, ct);
            throw new UploadProtocolException("size_mismatch", $"The provider stored {completed.SizeBytes} bytes, not the declared {session.SizeBytes}.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var content = await db.StoredObjects.IgnoreQueryFilters(AllTenants).FirstAsync(o => o.Id == session.ObjectId, ct);
        var parentAlive = session.ParentId is { } parentId
            && await db.Nodes.IgnoreQueryFilters(AllTenants).AnyAsync(n => n.Id == parentId && n.TenantId == session.TenantId && n.TrashedAt == null, ct);
        var parent = parentAlive ? session.ParentId : null;
        var name = await tree.FreeNameAsync(parent, session.FileName, excludeNodeId: null, ct);

        content.MarkAvailable();
        var node = Node.File(session.TenantId, parent, name, content, session.UserId, now);
        db.Replicas.Add(new Replica(session.TenantId, content.Id, account.Id, completed.ProviderObjectId, ReplicaStatus.Available, now));
        db.Nodes.Add(node);
        session.Complete(node.Id, now);
        await accounting.CommitUploadAsync(account.Id, session.SizeBytes, ct);
        await db.SaveChangesAsync(ct); // a concurrent completion fails here on the session's row version
        await transaction.CommitAsync(ct);
        return node;
    }

    /// <summary>Discards the upload at the provider (best effort) and releases the reservation.</summary>
    public async Task AbortAsync(UploadSession session, UploadSessionStatus outcome, CancellationToken ct)
    {
        if (session.Status != UploadSessionStatus.Pending) return;

        var (provider, account) = await ProviderForAsync(session, ct);
        try
        {
            await provider.AbortUploadAsync(account, Handle(session), ct);
        }
        catch (Exception e) when (e is StorageAuthException or StorageConnectionException or UploadProtocolException)
        {
            logger.LogInformation("Provider abort of upload {SessionId} failed; it will expire there: {Reason}", session.Id, e.Message);
        }

        session.End(outcome, clock.GetUtcNow());
        await db.StoredObjects.IgnoreQueryFilters(AllTenants)
            .Where(o => o.Id == session.ObjectId && o.Status == StoredObjectStatus.Pending)
            .ExecuteDeleteAsync(ct);
        await accounting.ReleaseAsync(account.Id, session.SizeBytes, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Expires abandoned sessions (all tenants). Returns how many were expired.</summary>
    public async Task<int> ExpireDueAsync(int batchSize, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.UploadSessions.IgnoreQueryFilters(AllTenants)
            .Where(s => s.Status == UploadSessionStatus.Pending && s.ExpiresAt <= now)
            .OrderBy(s => s.ExpiresAt)
            .Take(batchSize)
            .ToListAsync(ct);
        foreach (var session in due) await AbortAsync(session, UploadSessionStatus.Expired, ct);
        return due.Count;
    }

    private void EnsurePending(UploadSession session)
    {
        if (!session.IsPending(clock.GetUtcNow()))
            throw new UploadProtocolException("upload_not_pending", $"This upload is {session.Status.ToString().ToLowerInvariant()} or expired.");
    }

    private ProviderUpload Handle(UploadSession session) =>
        new(session.ObjectKey, protector.Unprotect(session.ProviderStateCiphertext, session.Id.ToString()), session.SizeBytes, session.ChunkSize);

    private async Task<(IStorageProvider Provider, StorageAccount Account)> ProviderForAsync(UploadSession session, CancellationToken ct)
    {
        var account = await db.StorageAccounts.IgnoreQueryFilters(AllTenants).FirstAsync(a => a.Id == session.StorageAccountId, ct);
        return (providers.Get(account.Provider), account);
    }
}
