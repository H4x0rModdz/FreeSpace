using FreeSpace.Domain.Files;
using FreeSpace.Infrastructure.Files;
using FreeSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Net.Http.Headers;

namespace FreeSpace.Api.Files;

/// <summary>
/// Streams a file's bytes from its provider, honoring a single HTTP Range (video seeking, resumable
/// downloads). User content is served with headers that stop browsers from executing it on this origin.
/// </summary>
public sealed class NodeContentResult(Node file, Guid tenantId, bool inline) : IActionResult
{
    /// <summary>Types a browser may render inline without being able to run script in our origin.</summary>
    private static readonly HashSet<string> InlineSafeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp",
        "application/pdf", "text/plain",
    };

    public static bool IsInlineSafe(string? mimeType) =>
        mimeType is not null && (InlineSafeTypes.Contains(mimeType)
                                 || mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                                 || mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var size = file.SizeBytes;

        ByteRange? range = null;
        if (TryParseRange(http.Request, size, out var requested, out var unsatisfiable))
        {
            if (unsatisfiable)
            {
                http.Response.Headers.ContentRange = $"bytes */{size}";
                await ContentProblems.WriteAsync(context, StatusCodes.Status416RangeNotSatisfiable, "range_not_satisfiable", "The requested range is outside the file.");
                return;
            }
            range = requested;
        }

        Stream content;
        try
        {
            content = await http.RequestServices.GetRequiredService<ContentReader>().OpenAsync(tenantId, file, range, ct);
        }
        catch (Exception e) when (ContentProblems.TryMap(e, out var status, out var code))
        {
            await ContentProblems.WriteAsync(context, status, code, e.Message);
            return;
        }

        await using (content)
        {
            var renderInline = inline && IsInlineSafe(file.MimeType);
            var response = http.Response;
            response.StatusCode = range is null ? StatusCodes.Status200OK : StatusCodes.Status206PartialContent;
            response.ContentType = file.MimeType ?? "application/octet-stream";
            response.ContentLength = range?.Length ?? size;
            if (range is { } r) response.Headers.ContentRange = $"bytes {r.From}-{r.To}/{size}";
            response.Headers.AcceptRanges = "bytes";
            response.Headers.ContentDisposition = renderInline ? ContentDispositions.Inline(file.Name) : ContentDispositions.Attachment(file.Name);
            ContentProblems.ApplyContentSecurityHeaders(response, sandbox: file.MimeType != "application/pdf");

            try
            {
                await content.CopyToAsync(response.Body, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Headers are gone already; cut the connection so the client sees a truncated transfer, not a "complete" file.
                http.RequestServices.GetRequiredService<ILogger<NodeContentResult>>().LogWarning(e, "Streaming node {NodeId} failed midway", file.Id);
                http.Abort();
            }
        }
    }

    /// <summary>Single "bytes=" ranges only; anything else (or none) means the whole file, as RFC 9110 allows.</summary>
    private static bool TryParseRange(HttpRequest request, long size, out ByteRange range, out bool unsatisfiable)
    {
        range = default;
        unsatisfiable = false;
        if (size == 0 || !RangeHeaderValue.TryParse(request.Headers.Range.ToString(), out var header)
            || !string.Equals(header.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) || header.Ranges.Count != 1)
            return false;

        var item = header.Ranges.First();
        long from, to;
        if (item.From is null)
        {
            if (item.To is not { } suffix || suffix == 0) return unsatisfiable = true;
            from = Math.Max(0, size - suffix);
            to = size - 1;
        }
        else
        {
            from = item.From.Value;
            to = Math.Min(item.To ?? size - 1, size - 1);
            if (from >= size || from > to) return unsatisfiable = true;
        }
        range = new ByteRange(from, to);
        return true;
    }
}

/// <summary>Error responses and safety headers shared by every endpoint that serves stored bytes.</summary>
public static class ContentProblems
{
    public static bool TryMap(Exception e, out int status, out string code)
    {
        (status, code) = e switch
        {
            ContentUnavailableException => (StatusCodes.Status503ServiceUnavailable, "content_unavailable"),
            StorageObjectMissingException => (StatusCodes.Status502BadGateway, "content_missing"),
            StorageAuthException => (StatusCodes.Status502BadGateway, "storage_auth_failed"),
            StorageConnectionException => (StatusCodes.Status502BadGateway, "storage_unavailable"),
            _ => (0, ""),
        };
        return status != 0;
    }

    public static Task WriteAsync(ActionContext context, int status, string code, string detail)
    {
        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateProblemDetails(context.HttpContext, status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } }.ExecuteResultAsync(context);
    }

    /// <summary>
    /// User-uploaded bytes must never run as this origin: no MIME sniffing, and a sandboxing CSP so even
    /// HTML/SVG opened directly cannot execute script. (Chrome's PDF viewer refuses sandboxed documents.)
    /// </summary>
    public static void ApplyContentSecurityHeaders(HttpResponse response, bool sandbox = true)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = sandbox ? "default-src 'none'; sandbox" : "default-src 'none'";
        response.Headers.CacheControl = "private, no-store";
    }
}
