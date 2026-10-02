using System.Text.Json;
using FreeSpace.Domain.Storage;
using FreeSpace.Infrastructure.Security;

namespace FreeSpace.Infrastructure.Storage;

/// <summary>The provider rejected the credentials (revoked/expired token, wrong keys). Needs a reconnect.</summary>
public sealed class StorageAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The provider could not be reached or refused the operation for a non-credential reason.</summary>
public sealed class StorageConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="UsedBytes">Null when the provider has no usage API; FreeSpace then tracks usage itself.</param>
public sealed record QuotaSnapshot(long? TotalBytes, long? UsedBytes);

/// <summary>
/// Operations every storage backend supports. Grows with each phase (uploads, reads, deletes).
/// Implementations throw <see cref="StorageAuthException"/> or <see cref="StorageConnectionException"/>.
/// </summary>
public interface IStorageProvider
{
    StorageProvider Provider { get; }

    /// <summary>Reads quota and, in doing so, proves the credentials still work.</summary>
    Task<QuotaSnapshot> GetQuotaAsync(StorageAccount account, CancellationToken ct);

    /// <summary>Best-effort cleanup at the provider when an account is removed (e.g. revoke OAuth grant).</summary>
    Task DisconnectAsync(StorageAccount account, CancellationToken ct);
}

public sealed class StorageProviderRegistry(IEnumerable<IStorageProvider> providers)
{
    private readonly Dictionary<StorageProvider, IStorageProvider> _providers = providers.ToDictionary(p => p.Provider);

    public IStorageProvider Get(StorageProvider provider) =>
        _providers.TryGetValue(provider, out var p) ? p : throw new InvalidOperationException($"No provider registered for {provider}.");
}

/// <summary>Typed (de)serialization of an account's encrypted credentials, bound to the account id.</summary>
public static class StorageSecrets
{
    public static void Write<T>(StorageAccount account, T secret, ISecretProtector protector) =>
        account.SetSecret(protector.Protect(JsonSerializer.Serialize(secret, JsonSerializerOptions.Web), account.Id.ToString()));

    public static T Read<T>(StorageAccount account, ISecretProtector protector) =>
        JsonSerializer.Deserialize<T>(protector.Unprotect(account.SecretCiphertext, account.Id.ToString()), JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException("Stored credentials are empty.");

    public static T ReadConfig<T>(StorageAccount account) =>
        JsonSerializer.Deserialize<T>(account.ConfigJson ?? "null", JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException("Storage account configuration is missing.");

    public static string WriteConfig<T>(T config) => JsonSerializer.Serialize(config, JsonSerializerOptions.Web);
}
