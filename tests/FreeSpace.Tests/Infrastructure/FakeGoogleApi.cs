using System.Collections.Concurrent;
using FreeSpace.Infrastructure.Storage;
using FreeSpace.Infrastructure.Storage.Google;

namespace FreeSpace.Tests.Infrastructure;

/// <summary>In-memory stand-in for Google OAuth + Drive quota.</summary>
public sealed class FakeGoogleApi : IGoogleApi
{
    private readonly ConcurrentDictionary<string, GoogleConnection> _codes = new();

    public GoogleQuota Quota { get; set; } = new(LimitBytes: 15L << 30, UsageBytes: 1L << 30);
    public ConcurrentDictionary<string, bool> RevokedTokens { get; } = new();
    public ConcurrentDictionary<string, bool> DeletedFiles { get; } = new();
    public string? LastState { get; private set; }

    public bool IsConfigured => true;

    /// <summary>Simulates the user consenting at Google: returns the code Google would send to the callback.</summary>
    public string Consent(string subject, string email, string? refreshToken = null)
    {
        var code = $"code-{Guid.NewGuid():N}";
        _codes[code] = new GoogleConnection(subject, email, "Drive " + email, refreshToken ?? $"refresh-{Guid.NewGuid():N}");
        return code;
    }

    public string BuildAuthorizationUrl(string state)
    {
        LastState = state;
        return $"https://accounts.google.test/o/oauth2/auth?state={Uri.EscapeDataString(state)}";
    }

    public Task<GoogleConnection> ExchangeCodeAsync(string code, CancellationToken ct) =>
        _codes.TryRemove(code, out var connection)
            ? Task.FromResult(connection)
            : throw new StorageAuthException("Unknown authorization code.");

    public Task<GoogleQuota> GetQuotaAsync(string refreshToken, CancellationToken ct) =>
        RevokedTokens.ContainsKey(refreshToken)
            ? throw new StorageAuthException("invalid_grant")
            : Task.FromResult(Quota);

    public Task DeleteFileAsync(string refreshToken, string fileId, CancellationToken ct)
    {
        if (RevokedTokens.ContainsKey(refreshToken)) throw new StorageAuthException("invalid_grant");
        DeletedFiles[fileId] = true;
        return Task.CompletedTask;
    }

    public Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        RevokedTokens[refreshToken] = true;
        return Task.CompletedTask;
    }
}
