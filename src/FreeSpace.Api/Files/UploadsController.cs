using System.ComponentModel.DataAnnotations;
using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Api.Configurations;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.Files;

/// <summary>
/// Chunked, resumable uploads. Start a session (space is reserved on the chosen account), send every
/// chunk either directly to the provider (Drive session URL / S3 presigned part URLs) or through
/// <c>PUT chunks/{index}</c>, then complete it to get the file node. Sessions belong to the user
/// who started them.
/// </summary>
[Route("api/v1/uploads")]
[Tags("Uploads")]
[MinimumRole(TenantRole.Member)]
public sealed class UploadsController(
    AppDbContext db, UploadService uploads, AuditLog audit, IOptions<StorageOptions> storageOptions, IOptions<AppOptions> appOptions) : SecureController
{
    [HttpPost]
    [ProducesResponseType<UploadResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Start(StartUploadRequest request, CancellationToken ct)
    {
        if (NodeName.Validate(request.FileName) is { } nameError) return BadRequestError("invalid_name", nameError);
        if (request.SizeBytes > storageOptions.Value.MaxUploadBytes)
            return BadRequestError("file_too_large", $"Files can be at most {storageOptions.Value.MaxUploadBytes} bytes.");
        if (request.ParentId is { } parentId
            && !await db.Nodes.AnyAsync(n => n.Id == parentId && n.Kind == NodeKind.Folder && n.TrashedAt == null, ct))
            return NotFoundError("folder_not_found", "Folder not found.");

        var mimeType = string.IsNullOrWhiteSpace(request.MimeType) ? "application/octet-stream" : request.MimeType.Trim();
        var command = new StartUploadCommand(TenantId, UserId, request.ParentId, request.FileName, mimeType, request.SizeBytes,
            FrontendOrigin(), TimeSpan.FromHours(storageOptions.Value.UploadSessionHours));

        return await Guarded(async () =>
        {
            var started = await uploads.StartAsync(command, ct);
            return Created($"/api/v1/uploads/{started.Session.Id}", ToResponse(started.Session, started.Account.Provider, started.DirectUrl));
        });
    }

    /// <summary>Session state plus what the provider already holds; use it to resume after an interruption.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<UploadProgressResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (await FindOwnedAsync(id, ct) is not { } session) return UploadNotFound();
        var provider = await ProviderOfAsync(session, ct);
        if (session.Status != UploadSessionStatus.Pending)
            return Ok(new UploadProgressResponse(ToResponse(session, provider, null), session.Status == UploadSessionStatus.Completed ? session.SizeBytes : 0, []));

        return await Guarded(async () =>
        {
            var progress = await uploads.ProgressAsync(session, ct);
            return Ok(new UploadProgressResponse(ToResponse(session, provider, null), progress.BytesReceived, progress.CompletedChunks));
        });
    }

    /// <summary>Uploads one chunk through the API. The body must be exactly the chunk's bytes (raw, not multipart).</summary>
    [HttpPut("{id:guid}/chunks/{index:int}")]
    [DisableRequestSizeLimit] // chunks can be hundreds of MB; the exact length is enforced below
    [ProducesResponseType<ChunkReceivedResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PutChunk(Guid id, int index, CancellationToken ct)
    {
        if (await FindOwnedAsync(id, ct) is not { } session) return UploadNotFound();
        if (Request.ContentLength is not { } length)
            return Error(StatusCodes.Status411LengthRequired, "length_required", "Send the chunk with a Content-Length header.");

        return await Guarded(async () =>
        {
            var progress = await uploads.UploadChunkAsync(session, index, length, Request.Body, ct);
            return Ok(new ChunkReceivedResponse(progress.BytesReceived, progress.CompletedChunks));
        });
    }

    /// <summary>S3 only: short-lived URLs to PUT chunks straight to the bucket (needs CORS on the bucket for browsers).</summary>
    [HttpPost("{id:guid}/chunk-urls")]
    [ProducesResponseType<List<ChunkUrlResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChunkUrls(Guid id, PresignChunksRequest request, CancellationToken ct)
    {
        if (await FindOwnedAsync(id, ct) is not { } session) return UploadNotFound();
        return await Guarded(async () =>
        {
            var urls = await uploads.PresignAsync(session, request.Chunks.Distinct().ToList(), ct);
            return Ok(urls.Select(u => new ChunkUrlResponse(u.Index, u.Url, u.ExpiresAt)));
        });
    }

    /// <summary>Verifies every byte arrived and publishes the file. A name clash in the folder adds a " (n)" suffix.</summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<NodeResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
    {
        if (await FindOwnedAsync(id, ct) is not { } session) return UploadNotFound();
        return await Guarded(async () =>
        {
            var node = await uploads.CompleteAsync(session, ct);
            audit.Record(TenantId, UserId, AuditActions.FileUploaded, "node", node.Id,
                new { node.Name, node.SizeBytes, storageAccountId = session.StorageAccountId });
            await db.SaveChangesAsync(ct);
            return Created($"/api/v1/nodes/{node.Id}", ResponseMappings.ToNodeResponse(node));
        });
    }

    /// <summary>Cancels the upload and releases the reserved space.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Abort(Guid id, CancellationToken ct)
    {
        if (await FindOwnedAsync(id, ct) is not { } session) return UploadNotFound();
        if (session.Status == UploadSessionStatus.Completed)
            return ConflictError("upload_not_pending", "This upload is already completed.");
        await uploads.AbortAsync(session, UploadSessionStatus.Aborted, ct);
        return NoContent();
    }

    private async Task<UploadSession?> FindOwnedAsync(Guid id, CancellationToken ct) =>
        await uploads.FindAsync(id, ct) is { } session && session.TenantId == TenantId && session.UserId == UserId ? session : null;

    private Task<StorageProvider> ProviderOfAsync(UploadSession session, CancellationToken ct) =>
        db.StorageAccounts.Where(a => a.Id == session.StorageAccountId).Select(a => a.Provider).FirstAsync(ct);

    private string? FrontendOrigin() =>
        Uri.TryCreate(appOptions.Value.FrontendUrl, UriKind.Absolute, out var url) ? url.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>Maps upload/provider failures to stable error codes.</summary>
    private async Task<IActionResult> Guarded(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UploadProtocolException e)
        {
            var status = e.Code switch
            {
                "insufficient_storage" => StatusCodes.Status507InsufficientStorage,
                "upload_expired" => StatusCodes.Status410Gone,
                "invalid_chunk" or "invalid_chunk_size" or "presign_not_supported" => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status409Conflict,
            };
            return Error(status, e.Code, e.Message);
        }
        catch (StorageAuthException e)
        {
            return Error(StatusCodes.Status502BadGateway, "storage_auth_failed", e.Message);
        }
        catch (StorageConnectionException e)
        {
            return Error(StatusCodes.Status502BadGateway, "storage_unavailable", e.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictError("upload_not_pending", "This upload was completed or cancelled concurrently.");
        }
    }

    private static UploadResponse ToResponse(UploadSession s, StorageProvider provider, string? directUrl) => new(
        s.Id, s.Status, s.ParentId, s.FileName, s.SizeBytes, s.MimeType, s.ChunkSize, s.ChunkCount,
        s.StorageAccountId, provider, directUrl, s.ExpiresAt, s.NodeId);

    private ObjectResult UploadNotFound() => NotFoundError("upload_not_found", "Upload not found.");
}
