using FreeSpace.Api.Auditing;
using FreeSpace.Api.Common;
using FreeSpace.Api.Configurations;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using FreeSpace.Infrastructure.Persistence;
using FreeSpace.Infrastructure.Security;
using FreeSpace.Infrastructure.Storage.Google;
using FreeSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.StorageAccounts;

/// <summary>
/// Google redirects the browser here without our bearer token, so this is not a <see cref="SecureController"/>:
/// the single-use OAuth state identifies tenant and user, and the initiator's role is re-checked.
/// </summary>
[Route("api/v1/storage-accounts/google/callback")]
[Tags("Storage accounts")]
[AllowAnonymous]
public sealed class GoogleOAuthCallbackController(
    AppDbContext db, IGoogleApi google, ISecretProtector protector, StorageAccountService service, AuditLog audit,
    IOptions<AppOptions> appOptions, TimeProvider clock, ILogger<GoogleOAuthCallbackController> logger) : BaseController
{
    [HttpGet, EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state)) return Outcome("invalid_state");

        var now = clock.GetUtcNow();
        var stateHash = SecureTokens.Hash(state);
        var oauthState = await db.OAuthStates.FirstOrDefaultAsync(s => s.StateHash == stateHash, ct);
        if (oauthState is null || !oauthState.IsUsable(now) || oauthState.Provider != StorageProvider.GoogleDrive)
            return Outcome("invalid_state", returnUrl: oauthState?.ReturnUrl);

        oauthState.Consume(now); // single use, even if the rest fails
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return Outcome(error == "access_denied" ? "access_denied" : "authorization_failed", returnUrl: oauthState.ReturnUrl);

        // Re-check the initiator is still allowed to manage storage in that tenant.
        var role = await db.Memberships
            .Where(m => m.TenantId == oauthState.TenantId && m.UserId == oauthState.UserId)
            .Select(m => (TenantRole?)m.Role).FirstOrDefaultAsync(ct);
        if (role is null || role < TenantRole.Admin)
            return Outcome("forbidden", returnUrl: oauthState.ReturnUrl);

        GoogleConnection connection;
        try
        {
            connection = await google.ExchangeCodeAsync(code, ct);
        }
        catch (StorageAuthException e)
        {
            logger.LogInformation("Google connect rejected: {Reason}", e.Message);
            return Outcome("consent_incomplete", returnUrl: oauthState.ReturnUrl);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Google code exchange failed");
            return Outcome("google_error", returnUrl: oauthState.ReturnUrl);
        }

        // Anonymous request: there is no tenant in context, so scope the lookup explicitly.
        var account = await db.StorageAccounts.IgnoreQueryFilters([AppDbContext.TenantFilter])
            .FirstOrDefaultAsync(a => a.TenantId == oauthState.TenantId && a.Provider == StorageProvider.GoogleDrive && a.ExternalAccountId == connection.Subject, ct);
        var created = account is null;
        if (account is null)
        {
            account = new StorageAccount(oauthState.TenantId, StorageProvider.GoogleDrive, connection.Subject, connection.Name ?? connection.Email,
                connection.Email, configJson: null, oauthState.UserId, now);
            db.StorageAccounts.Add(account);
        }
        else
        {
            account.Reconnect(account.DisplayName, connection.Email, account.ConfigJson, now);
        }
        StorageSecrets.Write(account, new GoogleSecret(connection.RefreshToken), protector);
        await service.SyncQuotaAsync(account, ct);

        audit.Record(oauthState.TenantId, oauthState.UserId, created ? AuditActions.StorageConnected : AuditActions.StorageReconnected,
            "storage_account", account.Id, new { provider = StorageProvider.GoogleDrive, email = connection.Email });
        await db.SaveChangesAsync(ct);

        return Outcome(status: null, account.Id, oauthState.ReturnUrl);
    }

    /// <summary>
    /// Sends the browser back to the native app (<paramref name="returnUrl"/>, validated when the flow
    /// started) or to the web app; with neither, answers with JSON.
    /// </summary>
    private IActionResult Outcome(string? status, Guid? accountId = null, string? returnUrl = null)
    {
        var outcome = status ?? "connected";
        var query = $"status={Uri.EscapeDataString(outcome)}" + (accountId is null ? "" : $"&accountId={accountId}");
        if (returnUrl is not null)
            return Redirect(returnUrl + (returnUrl.Contains('?') ? "&" : "?") + query);

        var frontendUrl = appOptions.Value.FrontendUrl;
        if (string.IsNullOrEmpty(frontendUrl))
            return status is null
                ? Ok(new { status = outcome, accountId })
                : BadRequestError(outcome, "Google Drive connection failed.");

        return Redirect($"{frontendUrl.TrimEnd('/')}/storage/google/callback?{query}");
    }
}
