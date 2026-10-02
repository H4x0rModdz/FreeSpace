namespace FreeSpace.Api.Common;

public static class ClientInfo
{
    public static string? IpAddress(this HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    public static string? UserAgent(this HttpContext http)
    {
        var value = http.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= 512 ? value : value[..512];
    }
}
