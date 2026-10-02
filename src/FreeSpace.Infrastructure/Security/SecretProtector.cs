using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FreeSpace.Infrastructure.Security;

public sealed class EncryptionOptions
{
    /// <summary>Base64 of a 32-byte key. Lives outside the database (env/secret store); never committed.</summary>
    [Required] public string Key { get; set; } = "";
}

/// <summary>Encrypts small secrets (provider credentials) for storage in the database.</summary>
public interface ISecretProtector
{
    /// <param name="associatedData">Binds the ciphertext to its owner (e.g. the row id) so it cannot be copied to another row.</param>
    string Protect(string plaintext, string associatedData);
    string Unprotect(string ciphertext, string associatedData);
}

/// <summary>
/// AES-256-GCM. Format: <c>v1:base64(nonce[12] | tag[16] | ciphertext)</c>. The version prefix leaves
/// room for key rotation (decrypt with the old key, re-encrypt with the new one).
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<EncryptionOptions> options)
    {
        try
        {
            _key = Convert.FromBase64String(options.Value.Key);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Encryption:Key must be base64.");
        }
        if (_key.Length != 32) throw new InvalidOperationException("Encryption:Key must decode to exactly 32 bytes.");
    }

    public string Protect(string plaintext, string associatedData)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + plainBytes.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Aad(associatedData));
        return Version + Convert.ToBase64String(output);
    }

    public string Unprotect(string ciphertext, string associatedData)
    {
        if (!ciphertext.StartsWith(Version, StringComparison.Ordinal))
            throw new CryptographicException("Unknown secret format.");

        var input = Convert.FromBase64String(ciphertext[Version.Length..]);
        if (input.Length < NonceSize + TagSize) throw new CryptographicException("Secret is truncated.");

        var plainBytes = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plainBytes, Aad(associatedData));
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] Aad(string associatedData) => Encoding.UTF8.GetBytes("freespace:" + associatedData);
}
