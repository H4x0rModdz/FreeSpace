using System.Net;

namespace FreeSpace.Api.Common;

/// <summary>
/// Where browser-based flows (OAuth) may send the user back to a native app, following RFC 8252:
/// a loopback IP on any port, or a custom scheme the app registered. Anything else would be an
/// open redirect, so it is refused.
/// </summary>
public static class NativeRedirects
{
    public static bool IsAllowed(string url, IReadOnlyCollection<string> customSchemes)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        // Loopback must be an IP literal: "localhost" can be re-pointed, and "localhost.evil.com" is not local at all.
        if (uri.Scheme == Uri.UriSchemeHttp)
            return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);

        return uri.Scheme is not ("https" or "javascript" or "data" or "file")
               && customSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);
    }
}
