using System.Net;
using System.Security.Cryptography;
using FreeSpace.Infrastructure.Security;
using FreeSpace.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace FreeSpace.Tests;

public sealed class SecurityPrimitivesTests
{
    private static AesGcmSecretProtector Protector(byte fill = 7) =>
        new(Options.Create(new EncryptionOptions { Key = Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()) }));

    [Fact]
    public void Secrets_round_trip()
    {
        var protector = Protector();
        var ciphertext = protector.Protect("refresh-token", "account-1");

        Assert.StartsWith("v1:", ciphertext);
        Assert.DoesNotContain("refresh-token", ciphertext);
        Assert.Equal("refresh-token", protector.Unprotect(ciphertext, "account-1"));
    }

    [Fact]
    public void Ciphertext_is_bound_to_its_owner()
    {
        var protector = Protector();
        var ciphertext = protector.Protect("refresh-token", "account-1");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(ciphertext, "account-2"));
    }

    [Fact]
    public void Wrong_key_or_tampering_is_detected()
    {
        var ciphertext = Protector().Protect("refresh-token", "account-1");
        var bytes = Convert.FromBase64String(ciphertext[3..]);
        bytes[^1] ^= 0xFF;
        var tampered = "v1:" + Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => Protector(fill: 9).Unprotect(ciphertext, "account-1"));
        Assert.ThrowsAny<CryptographicException>(() => Protector().Unprotect(tampered, "account-1"));
    }

    [Fact]
    public void Key_must_be_32_bytes() =>
        Assert.Throws<InvalidOperationException>(() => new AesGcmSecretProtector(Options.Create(new EncryptionOptions { Key = Convert.ToBase64String(new byte[16]) })));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    public void Ssrf_guard_classifies_addresses(string address, bool isPublic) =>
        Assert.Equal(isPublic, SsrfGuard.IsPublic(IPAddress.Parse(address)));
}
