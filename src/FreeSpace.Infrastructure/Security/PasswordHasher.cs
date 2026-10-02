using Isopoh.Cryptography.Argon2;

namespace FreeSpace.Infrastructure.Security;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
    /// <summary>Spends the same time as a real verification; used when the user does not exist.</summary>
    void SimulateVerify(string password);
}

/// <summary>Argon2id with the OWASP minimum parameters (19 MiB, 2 iterations, 1 lane).</summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int MemoryCostKiB = 19 * 1024;
    private const int TimeCost = 2;
    private const int Parallelism = 1;

    private readonly Lazy<string> _dummyHash;

    public Argon2PasswordHasher() => _dummyHash = new(() => Hash(Guid.NewGuid().ToString()));

    public string Hash(string password) =>
        Argon2.Hash(password, timeCost: TimeCost, memoryCost: MemoryCostKiB, parallelism: Parallelism, type: Argon2Type.HybridAddressing);

    public bool Verify(string hash, string password) => Argon2.Verify(hash, password);

    public void SimulateVerify(string password) => Argon2.Verify(_dummyHash.Value, password);
}
