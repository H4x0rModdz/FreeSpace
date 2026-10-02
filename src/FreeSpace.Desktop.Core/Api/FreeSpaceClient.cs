using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpace.Contracts.Auth;
using FreeSpace.Contracts.Files;
using FreeSpace.Contracts.Storage;
using FreeSpace.Contracts.Tenants;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Desktop.Core.Api;

/// <summary>An error answered by the API, with its stable <see cref="Code"/> (ProblemDetails "code").</summary>
public sealed class ApiException(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Where the refresh token survives restarts (DPAPI on Windows; memory in tests).</summary>
public interface ISessionStore
{
    StoredSession? Load();
    void Save(StoredSession session);
    void Clear();
}

public sealed record StoredSession(string ServerUrl, string RefreshToken);

public sealed class InMemorySessionStore : ISessionStore
{
    private StoredSession? _session;
    public StoredSession? Load() => _session;
    public void Save(StoredSession session) => _session = session;
    public void Clear() => _session = null;
}

/// <summary>
/// Typed client for the FreeSpace API. Keeps the access token in memory and the refresh token in the
/// <see cref="ISessionStore"/>. On a 401 it refreshes once and retries; refreshes are serialized because
/// the server rotates refresh tokens and treats a replayed one as theft.
/// </summary>
public sealed class FreeSpaceClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;
    private readonly ISessionStore _store;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;

    public FreeSpaceClient(Uri serverUrl, ISessionStore store, HttpMessageHandler? handler = null)
    {
        ServerUrl = serverUrl;
        _store = store;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = serverUrl;
        _http.Timeout = Timeout.InfiniteTimeSpan; // uploads/downloads stream for as long as they need
    }

    public Uri ServerUrl { get; }
    public bool IsSignedIn => _refreshToken is not null;

    /// <summary>Raised when the session is gone (logout, revoked, refresh rejected) so the UI can return to login.</summary>
    public event EventHandler? SessionEnded;

    // ---- Session -------------------------------------------------------------------------------

    public async Task LoginAsync(string email, string password, CancellationToken ct = default) =>
        Adopt(await SendAsync<TokenPair>(HttpMethod.Post, "api/v1/auth/login", new { email, password }, authenticated: false, ct));

    public async Task RegisterAsync(string name, string email, string password, CancellationToken ct = default) =>
        Adopt(await SendAsync<TokenPair>(HttpMethod.Post, "api/v1/auth/register", new { name, email, password }, authenticated: false, ct));

    /// <summary>Restores a saved session (if any) by refreshing it. False when there is none or it was revoked.</summary>
    public async Task<bool> TryResumeAsync(CancellationToken ct = default)
    {
        if (_store.Load() is not { } saved || !string.Equals(saved.ServerUrl, ServerUrl.ToString(), StringComparison.OrdinalIgnoreCase))
            return false;
        _refreshToken = saved.RefreshToken;
        return await RefreshAsync(staleAccessToken: null, ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Post, "api/v1/auth/logout", null, ct);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            // Signing out locally must work even if the server is unreachable.
        }
        EndSession();
    }

    public Task<MeResponse> MeAsync(CancellationToken ct = default) => GetAsync<MeResponse>("api/v1/me", ct);

    public Task<List<TenantSummary>> TenantsAsync(CancellationToken ct = default) => GetAsync<List<TenantSummary>>("api/v1/tenants", ct);

    public async Task SwitchTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        _accessToken = (await SendAsync<AccessTokenResponse>(HttpMethod.Post, "api/v1/auth/switch-tenant", new { tenantId }, authenticated: true, ct)).AccessToken;

    // ---- Storage accounts ----------------------------------------------------------------------

    public Task<List<StorageAccountResponse>> StorageAccountsAsync(CancellationToken ct = default) => GetAsync<List<StorageAccountResponse>>("api/v1/storage-accounts", ct);
    public Task<StorageSummaryResponse> StorageSummaryAsync(CancellationToken ct = default) => GetAsync<StorageSummaryResponse>("api/v1/storage-accounts/summary", ct);
    public Task<StorageAccountResponse> ConnectS3Async(ConnectS3Request request, CancellationToken ct = default) => SendAsync<StorageAccountResponse>(HttpMethod.Post, "api/v1/storage-accounts/s3", request, true, ct);
    public Task<GoogleAuthorizationResponse> AuthorizeGoogleAsync(string? returnUrl, CancellationToken ct = default) => SendAsync<GoogleAuthorizationResponse>(HttpMethod.Post, "api/v1/storage-accounts/google/authorize", new { returnUrl }, true, ct);
    public Task<StorageAccountResponse> SyncStorageAccountAsync(Guid id, CancellationToken ct = default) => SendAsync<StorageAccountResponse>(HttpMethod.Post, $"api/v1/storage-accounts/{id}/sync", null, true, ct);
    public Task<StorageAccountResponse> UpdateStorageAccountAsync(Guid id, UpdateStorageAccountRequest request, CancellationToken ct = default) => SendAsync<StorageAccountResponse>(HttpMethod.Patch, $"api/v1/storage-accounts/{id}", request, true, ct);
    public Task RemoveStorageAccountAsync(Guid id, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"api/v1/storage-accounts/{id}", null, ct);

    // ---- File tree -----------------------------------------------------------------------------

    public Task<NodePageResponse> ListAsync(Guid? parentId, string? cursor = null, int limit = 200, CancellationToken ct = default) =>
        GetAsync<NodePageResponse>($"api/v1/nodes?limit={limit}{(parentId is null ? "" : $"&parentId={parentId}")}{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}", ct);

    /// <summary>Every child of a folder, following pagination.</summary>
    public async Task<List<NodeResponse>> ListAllAsync(Guid? parentId, CancellationToken ct = default)
    {
        var all = new List<NodeResponse>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(parentId, cursor, 500, ct);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return all;
    }

    public Task<NodeDetailsResponse> NodeAsync(Guid id, CancellationToken ct = default) => GetAsync<NodeDetailsResponse>($"api/v1/nodes/{id}", ct);
    public Task<List<NodeResponse>> SearchAsync(string query, CancellationToken ct = default) => GetAsync<List<NodeResponse>>($"api/v1/nodes/search?q={Uri.EscapeDataString(query)}", ct);
    public Task<NodeResponse> CreateFolderAsync(Guid? parentId, string name, CancellationToken ct = default) => SendAsync<NodeResponse>(HttpMethod.Post, "api/v1/nodes/folders", new { parentId, name }, true, ct);
    public Task<NodeResponse> RenameAsync(Guid id, string name, CancellationToken ct = default) => SendAsync<NodeResponse>(HttpMethod.Patch, $"api/v1/nodes/{id}", new { name }, true, ct);
    public Task<NodeResponse> MoveAsync(Guid id, Guid? parentId, CancellationToken ct = default) => SendAsync<NodeResponse>(HttpMethod.Post, $"api/v1/nodes/{id}/move", new { parentId }, true, ct);
    public Task TrashAsync(Guid id, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/v1/nodes/{id}/trash", null, ct);
    public Task<ContentLinkResponse> ContentLinkAsync(Guid id, bool inline = false, CancellationToken ct = default) => SendAsync<ContentLinkResponse>(HttpMethod.Post, $"api/v1/nodes/{id}/content-link?inline={inline}", null, true, ct);

    public Task<List<TrashItemResponse>> TrashListAsync(CancellationToken ct = default) => GetAsync<List<TrashItemResponse>>("api/v1/trash", ct);
    public Task<NodeResponse> RestoreAsync(Guid id, CancellationToken ct = default) => SendAsync<NodeResponse>(HttpMethod.Post, $"api/v1/trash/{id}/restore", null, true, ct);
    public Task DeleteForeverAsync(Guid id, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"api/v1/trash/{id}", null, ct);
    public Task EmptyTrashAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Delete, "api/v1/trash", null, ct);

    public Task<CreatedShareResponse> ShareAsync(Guid nodeId, DateTimeOffset? expiresAt, CancellationToken ct = default) => SendAsync<CreatedShareResponse>(HttpMethod.Post, $"api/v1/nodes/{nodeId}/shares", new { expiresAt }, true, ct);
    public Task<List<ShareResponse>> SharesAsync(CancellationToken ct = default) => GetAsync<List<ShareResponse>>("api/v1/shares", ct);
    public Task RevokeShareAsync(Guid id, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"api/v1/shares/{id}", null, ct);

    // ---- Transfers -----------------------------------------------------------------------------

    public Task<UploadResponse> StartUploadAsync(StartUploadRequest request, CancellationToken ct = default) => SendAsync<UploadResponse>(HttpMethod.Post, "api/v1/uploads", request, true, ct);
    public Task<UploadProgressResponse> UploadProgressAsync(Guid uploadId, CancellationToken ct = default) => GetAsync<UploadProgressResponse>($"api/v1/uploads/{uploadId}", ct);
    public Task<List<ChunkUrlResponse>> ChunkUrlsAsync(Guid uploadId, IReadOnlyCollection<int> chunks, CancellationToken ct = default) => SendAsync<List<ChunkUrlResponse>>(HttpMethod.Post, $"api/v1/uploads/{uploadId}/chunk-urls", new { chunks }, true, ct);
    public Task<NodeResponse> CompleteUploadAsync(Guid uploadId, CancellationToken ct = default) => SendAsync<NodeResponse>(HttpMethod.Post, $"api/v1/uploads/{uploadId}/complete", null, true, ct);
    public Task AbortUploadAsync(Guid uploadId, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"api/v1/uploads/{uploadId}", null, ct);

    /// <summary>Sends one chunk through the API (used when the provider cannot be reached directly).</summary>
    public async Task PutChunkAsync(Guid uploadId, int index, Func<Stream> openChunk, long length, CancellationToken ct = default)
    {
        using var response = await SendWithAuthAsync(() =>
        {
            var content = new StreamContent(openChunk());
            content.Headers.ContentLength = length;
            return new HttpRequestMessage(HttpMethod.Put, $"api/v1/uploads/{uploadId}/chunks/{index}") { Content = content };
        }, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Opens a file's content stream (optionally from <paramref name="offset"/>, for resuming).</summary>
    public async Task<(Stream Content, long? Length)> OpenContentAsync(Guid nodeId, long offset = 0, CancellationToken ct = default)
    {
        var response = await SendWithAuthAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/nodes/{nodeId}/content");
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadAsStreamAsync(ct), response.Content.Headers.ContentLength);
    }

    // ---- Plumbing ------------------------------------------------------------------------------

    private void Adopt(TokenPair tokens)
    {
        _accessToken = tokens.AccessToken;
        _refreshToken = tokens.RefreshToken;
        _store.Save(new StoredSession(ServerUrl.ToString(), tokens.RefreshToken));
    }

    private void EndSession()
    {
        var wasSignedIn = _refreshToken is not null;
        _accessToken = _refreshToken = null;
        _store.Clear();
        if (wasSignedIn) SessionEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <returns>False when the session cannot be refreshed (it has been ended).</returns>
    private async Task<bool> RefreshAsync(string? staleAccessToken, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && _accessToken != staleAccessToken) return true; // another caller already refreshed
            if (_refreshToken is null) return false;

            using var response = await _http.PostAsJsonAsync("api/v1/auth/refresh", new { refreshToken = _refreshToken }, Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                EndSession();
                return false;
            }
            Adopt((await response.Content.ReadFromJsonAsync<TokenPair>(Json, ct))!);
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(Func<HttpRequestMessage> build, HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (_accessToken is null && !await RefreshAsync(null, ct))
                throw new ApiException(HttpStatusCode.Unauthorized, "not_signed_in", "You are not signed in.");

            var token = _accessToken;
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await _http.SendAsync(request, completion, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;

            response.Dispose();
            if (!await RefreshAsync(token, ct))
                throw new ApiException(HttpStatusCode.Unauthorized, "session_expired", "Your session has ended. Sign in again.");
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendWithAuthAsync(() => new HttpRequestMessage(HttpMethod.Get, path), HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        HttpRequestMessage Build() => new(method, path) { Content = body is null ? null : JsonContent.Create(body, options: Json) };

        using var response = authenticated
            ? await SendWithAuthAsync(Build, HttpCompletionOption.ResponseContentRead, ct)
            : await _http.SendAsync(Build(), ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendWithAuthAsync(
            () => new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: Json) },
            HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string code = "http_" + (int)response.StatusCode, message = response.ReasonPhrase ?? "Request failed.";
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (problem.RootElement.TryGetProperty("code", out var c) && c.GetString() is { } parsedCode) code = parsedCode;
            if (problem.RootElement.TryGetProperty("detail", out var d) && d.GetString() is { } detail) message = detail;
            else if (problem.RootElement.TryGetProperty("errors", out var errors)) message = FirstValidationError(errors) ?? message;
        }
        catch (JsonException)
        {
            // Not a ProblemDetails body; keep the status-based message.
        }
        response.Dispose();
        throw new ApiException(response.StatusCode, code, message);
    }

    private static string? FirstValidationError(JsonElement errors) =>
        errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray()).Select(v => v.GetString()).FirstOrDefault();

    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }
}
