using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Google;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Options;

namespace FreeSpace.Infrastructure.Storage.Google;

public sealed class GoogleOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Must match an authorized redirect URI in Google Cloud Console.</summary>
    public string RedirectUri { get; set; } = "";

    public bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0 && RedirectUri.Length > 0;
}

public sealed record GoogleConnection(string Subject, string Email, string? Name, string RefreshToken);
public sealed record GoogleQuota(long? LimitBytes, long UsageBytes);

/// <param name="FileId">Set once Google has the whole file.</param>
public sealed record ResumableStatus(long BytesReceived, string? FileId, long? FileSize);

/// <summary>The slice of Google's APIs FreeSpace uses; the seam for faking Google in tests.</summary>
public interface IGoogleApi
{
    bool IsConfigured { get; }
    string BuildAuthorizationUrl(string state);
    Task<GoogleConnection> ExchangeCodeAsync(string code, CancellationToken ct);
    Task<GoogleQuota> GetQuotaAsync(string refreshToken, CancellationToken ct);
    /// <summary>Deletes a Drive file; succeeds if it no longer exists.</summary>
    Task DeleteFileAsync(string refreshToken, string fileId, CancellationToken ct);

    /// <summary>Id of the "FreeSpace" folder in the user's Drive root, created on first use.</summary>
    Task<string> EnsureAppFolderAsync(string refreshToken, CancellationToken ct);

    /// <summary>Opens a resumable upload; the returned session URI itself authorizes the chunk requests.</summary>
    Task<Uri> StartResumableUploadAsync(string refreshToken, string name, string folderId, string mimeType, long size, string? origin, CancellationToken ct);

    Task<ResumableStatus> GetResumableStatusAsync(Uri session, long size, CancellationToken ct);

    Task<ResumableStatus> PutResumableChunkAsync(Uri session, long offset, long length, long size, Stream content, CancellationToken ct);

    Task CancelResumableUploadAsync(Uri session, CancellationToken ct);
    Task RevokeAsync(string refreshToken, CancellationToken ct);
}

public sealed class GoogleApi(IOptions<GoogleOptions> options, IHttpClientFactory httpClients) : IGoogleApi
{
    /// <summary>Named HttpClient for resumable-upload traffic (long timeout, no redirect following: 308 means "resume").</summary>
    public const string UploadHttpClient = "google-upload";
    private const string AppFolderName = "FreeSpace";
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    /// <summary>
    /// <c>drive.file</c> only grants access to files this app created, never the user's whole Drive.
    /// It is also a non-sensitive scope, so the OAuth app needs no Google security review.
    /// </summary>
    public static readonly string DriveScope = DriveService.Scope.DriveFile;

    private static readonly string[] Scopes = ["openid", "email", "profile", DriveScope];
    private const string FlowUserId = "freespace";

    private GoogleOptions Options => options.Value;

    public bool IsConfigured => Options.IsConfigured;

    private GoogleAuthorizationCodeFlow CreateFlow() => new(new GoogleAuthorizationCodeFlow.Initializer
    {
        ClientSecrets = new ClientSecrets { ClientId = Options.ClientId, ClientSecret = Options.ClientSecret },
        Scopes = Scopes,
    });

    public string BuildAuthorizationUrl(string state)
    {
        var request = (GoogleAuthorizationCodeRequestUrl)CreateFlow().CreateAuthorizationCodeRequest(Options.RedirectUri);
        request.State = state;
        request.AccessType = "offline"; // we need a refresh token
        request.Prompt = "consent";     // ...and Google only re-issues one on explicit consent
        return request.Build().AbsoluteUri;
    }

    public async Task<GoogleConnection> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var flow = CreateFlow();
        TokenResponse token;
        try
        {
            token = await flow.ExchangeCodeForTokenAsync(FlowUserId, code, Options.RedirectUri, ct);
        }
        catch (TokenResponseException e)
        {
            throw new StorageAuthException($"Google rejected the authorization code: {e.Error.Error}.", e);
        }

        var granted = (token.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!granted.Contains(DriveScope))
            throw new StorageAuthException("Google Drive access was not granted. Reconnect and allow access to files created by FreeSpace.");
        if (string.IsNullOrEmpty(token.RefreshToken))
            throw new StorageAuthException("Google did not return a refresh token.");

        var identity = await GoogleJsonWebSignature.ValidateAsync(token.IdToken,
            new GoogleJsonWebSignature.ValidationSettings { Audience = [Options.ClientId] });
        return new GoogleConnection(identity.Subject, identity.Email, identity.Name, token.RefreshToken);
    }

    public Task<GoogleQuota> GetQuotaAsync(string refreshToken, CancellationToken ct) =>
        WithDriveAsync(refreshToken, async drive =>
        {
            var request = drive.About.Get();
            request.Fields = "storageQuota(limit,usage)";
            var about = await request.ExecuteAsync(ct);
            // A null limit means unlimited storage (some Workspace plans).
            return new GoogleQuota(about.StorageQuota?.Limit, about.StorageQuota?.Usage ?? 0);
        });

    public Task DeleteFileAsync(string refreshToken, string fileId, CancellationToken ct) =>
        WithDriveAsync(refreshToken, async drive =>
        {
            try
            {
                await drive.Files.Delete(fileId).ExecuteAsync(ct);
            }
            catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
            {
                // Already gone (deleted by the user in Drive): deletion is idempotent.
            }
            return true;
        });

    public Task<string> EnsureAppFolderAsync(string refreshToken, CancellationToken ct) =>
        WithDriveAsync(refreshToken, async drive =>
        {
            // drive.file only lists folders this app created, so a user's own "FreeSpace" folder is never picked up.
            var list = drive.Files.List();
            list.Q = $"name = '{AppFolderName}' and mimeType = '{FolderMimeType}' and 'root' in parents and trashed = false";
            list.Fields = "files(id)";
            list.PageSize = 1;
            var existing = (await list.ExecuteAsync(ct)).Files?.FirstOrDefault()?.Id;
            if (existing is not null) return existing;

            var create = drive.Files.Create(new global::Google.Apis.Drive.v3.Data.File { Name = AppFolderName, MimeType = FolderMimeType, Parents = ["root"] });
            create.Fields = "id";
            return (await create.ExecuteAsync(ct)).Id;
        });

    public async Task<Uri> StartResumableUploadAsync(string refreshToken, string name, string folderId, string mimeType, long size, string? origin, CancellationToken ct)
    {
        var accessToken = await WithDriveAsync(refreshToken, drive =>
            ((UserCredential)drive.HttpClientInitializer).GetAccessTokenForRequestAsync(cancellationToken: ct));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id,size")
        {
            Content = JsonContent.Create(new { name, parents = new[] { folderId } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Upload-Content-Type", mimeType);
        request.Headers.Add("X-Upload-Content-Length", size.ToString(CultureInfo.InvariantCulture));
        // Google ties CORS for the session URI to the origin that opened it; needed for direct browser uploads.
        if (!string.IsNullOrEmpty(origin)) request.Headers.Add("Origin", origin);

        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new StorageAuthException("Google rejected the credentials; reconnect the account.");
        if (!response.IsSuccessStatusCode || response.Headers.Location is null)
            throw new StorageConnectionException($"Google refused to open the upload ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        return response.Headers.Location;
    }

    public async Task<ResumableStatus> GetResumableStatusAsync(Uri session, long size, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent([]) };
        request.Content.Headers.ContentRange = ContentRangeHeaderValue.Parse($"bytes */{size}");
        using var response = await SendAsync(request, ct);
        return await ReadStatusAsync(response, ct);
    }

    public async Task<ResumableStatus> PutResumableChunkAsync(Uri session, long offset, long length, long size, Stream content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new StreamContent(content) };
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, size);
        using var response = await SendAsync(request, ct);
        return await ReadStatusAsync(response, ct);
    }

    public async Task CancelResumableUploadAsync(Uri session, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, session);
        using var _ = await SendAsync(request, ct); // Google answers 499; anything is fine here
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClients.CreateClient(UploadHttpClient).SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new StorageConnectionException($"Could not reach Google: {e.Message}", e);
        }
    }

    /// <summary>308 = incomplete (Range says how much arrived); 200/201 = done (body has the file); 404/410 = session gone.</summary>
    private static async Task<ResumableStatus> ReadStatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        switch ((int)response.StatusCode)
        {
            case 308:
                return new ResumableStatus(ReceivedBytes(response), null, null);
            case 200 or 201:
                var file = await response.Content.ReadFromJsonAsync<DriveFileRef>(ct)
                           ?? throw new StorageConnectionException("Google returned an empty upload result.");
                long? fileSize = long.TryParse(file.Size, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                return new ResumableStatus(fileSize ?? 0, file.Id, fileSize);
            case 404 or 410:
                throw new UploadProtocolException("upload_expired", "The Google upload session expired; start a new upload.");
            default:
                throw new StorageConnectionException($"Google upload request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    /// <summary>Parses "Range: bytes=0-N"; no header means nothing arrived yet.</summary>
    private static long ReceivedBytes(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var range = values.FirstOrDefault() ?? "";
        var dash = range.LastIndexOf('-');
        return dash > 0 && long.TryParse(range[(dash + 1)..], CultureInfo.InvariantCulture, out var lastByte) ? lastByte + 1 : 0;
    }

    private sealed record DriveFileRef(string Id, string? Size);

    /// <summary>Runs a Drive call with the account's credentials and maps Google failures to storage exceptions.</summary>
    private async Task<T> WithDriveAsync<T>(string refreshToken, Func<DriveService, Task<T>> call)
    {
        using var flow = CreateFlow();
        var credential = new UserCredential(flow, FlowUserId, new TokenResponse { RefreshToken = refreshToken });
        using var drive = new DriveService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "FreeSpace" });

        try
        {
            return await call(drive);
        }
        catch (TokenResponseException e) when (e.Error.Error is "invalid_grant" or "unauthorized_client")
        {
            throw new StorageAuthException("Google access was revoked or expired; reconnect the account.", e);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            throw new StorageAuthException("Google rejected the credentials; reconnect the account.", e);
        }
        catch (Exception e) when (e is GoogleApiException or TokenResponseException or HttpRequestException)
        {
            throw new StorageConnectionException($"Google Drive request failed: {e.Message}", e);
        }
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        using var flow = CreateFlow();
        await flow.RevokeTokenAsync(FlowUserId, refreshToken, ct);
    }
}
