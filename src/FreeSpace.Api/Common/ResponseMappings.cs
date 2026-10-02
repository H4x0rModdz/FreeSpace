using System.Text.Json;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;

namespace FreeSpace.Api.Common;

/// <summary>Entity → contract mappings that need more than a constructor call.</summary>
public static class ResponseMappings
{
    public static NodeResponse ToNodeResponse(Node n) =>
        new(n.Id, n.ParentId, n.Kind, n.Name, n.SizeBytes, n.MimeType, n.CreatedAt, n.UpdatedAt);

    // ConfigJson never holds secrets (those live encrypted in SecretCiphertext), so it is safe to expose.
    public static StorageAccountResponse ToAccountResponse(StorageAccount a) => new(
        a.Id, a.Provider, a.DisplayName, a.Email, a.Status, a.Priority, a.TotalBytes, a.UsedBytes, a.AvailableBytes,
        a.LastQuotaSyncAt, a.LastError, a.ConfigJson is null ? null : JsonDocument.Parse(a.ConfigJson).RootElement, a.CreatedAt);
}
