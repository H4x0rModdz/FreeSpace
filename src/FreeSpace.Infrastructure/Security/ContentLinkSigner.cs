using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FreeSpace.Infrastructure.Security;

/// <summary>
/// Stateless, short-lived download links: <c>base64url(nodeId|tenantId|expiry).base64url(HMAC)</c>.
/// Used where a client cannot send an Authorization header (media players, web views, &lt;img&gt;).
/// The HMAC key is derived from the encryption key, so no extra secret has to be configured.
/// </summary>
public sealed class ContentLinkSigner
{
    private const int PayloadSize = 16 + 16 + 8;
    private readonly byte[] _key;

    public ContentLinkSigner(IOptions<EncryptionOptions> options) =>
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromBase64String(options.Value.Key), 32,
            info: Encoding.UTF8.GetBytes("freespace:content-links:v1"));

    public string Sign(Guid nodeId, Guid tenantId, DateTimeOffset expiresAt)
    {
        var payload = new byte[PayloadSize];
        nodeId.TryWriteBytes(payload.AsSpan(0, 16));
        tenantId.TryWriteBytes(payload.AsSpan(16, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(32), expiresAt.ToUnixTimeSeconds());
        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(HMACSHA256.HashData(_key, payload))}";
    }

    public bool TryVerify(string token, DateTimeOffset now, out Guid nodeId, out Guid tenantId)
    {
        nodeId = tenantId = Guid.Empty;
        var dot = token.IndexOf('.');
        if (dot <= 0) return false;

        byte[] payload, signature;
        try
        {
            payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            signature = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload.Length != PayloadSize || !CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_key, payload)))
            return false;
        if (DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(32))) <= now)
            return false;

        nodeId = new Guid(payload.AsSpan(0, 16));
        tenantId = new Guid(payload.AsSpan(16, 16));
        return true;
    }
}
