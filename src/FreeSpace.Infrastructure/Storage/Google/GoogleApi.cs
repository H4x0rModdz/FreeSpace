using System.Net;
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

/// <summary>The slice of Google's APIs FreeSpace uses; the seam for faking Google in tests.</summary>
public interface IGoogleApi
{
    bool IsConfigured { get; }
    string BuildAuthorizationUrl(string state);
    Task<GoogleConnection> ExchangeCodeAsync(string code, CancellationToken ct);
    Task<GoogleQuota> GetQuotaAsync(string refreshToken, CancellationToken ct);
    /// <summary>Deletes a Drive file; succeeds if it no longer exists.</summary>
    Task DeleteFileAsync(string refreshToken, string fileId, CancellationToken ct);
    Task RevokeAsync(string refreshToken, CancellationToken ct);
}

public sealed class GoogleApi(IOptions<GoogleOptions> options) : IGoogleApi
{
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
