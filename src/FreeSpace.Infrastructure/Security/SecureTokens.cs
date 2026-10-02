using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace FreeSpace.Infrastructure.Security;

/// <summary>Random opaque tokens (refresh, invite, share) and their at-rest hashes.</summary>
public static class SecureTokens
{
    public static string Generate(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool HashEquals(string hashA, string hashB) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hashA), Encoding.ASCII.GetBytes(hashB));
}
