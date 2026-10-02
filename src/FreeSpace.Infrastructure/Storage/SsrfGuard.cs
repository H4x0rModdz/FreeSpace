using System.Net;
using System.Net.Sockets;

namespace FreeSpace.Infrastructure.Storage;

/// <summary>
/// Prevents user-supplied endpoints (custom S3 URLs) from reaching internal networks, cloud metadata
/// services, or loopback. The check runs on the resolved IP at connect time, so DNS rebinding
/// (public name at validation, private IP at request time) is also blocked.
/// </summary>
public static class SsrfGuard
{
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),   // carrier-grade NAT
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),  // link-local, incl. cloud metadata 169.254.169.254
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("224.0.0.0/4"),     // multicast
        IPNetwork.Parse("240.0.0.0/4"),     // reserved + broadcast
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),        // unique local
        IPNetwork.Parse("fe80::/10"),       // link-local
        IPNetwork.Parse("ff00::/8"),        // multicast
    ];

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return !BlockedNetworks.Any(n => n.Contains(address));
    }

    /// <summary>An HTTP handler that refuses to connect to non-public addresses unless <paramref name="allowPrivate"/>.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = allowPrivate ? addresses : addresses.Where(IsPublic).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException($"Endpoint '{context.DnsEndPoint.Host}' resolves to a non-public address, which is not allowed.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
