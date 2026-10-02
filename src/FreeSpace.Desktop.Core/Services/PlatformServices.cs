using System.Net;
using System.Text;
using System.Web;
using FreeSpace.Contracts.Storage;

namespace FreeSpace.Desktop.Core.Services;

/// <summary>Dialogs and pickers; implemented by the UI layer so view models stay testable.</summary>
public interface IDialogService
{
    Task<string?> PromptAsync(string title, string message, string initialValue = "");
    Task<bool> ConfirmAsync(string title, string message);
    Task ShowMessageAsync(string title, string message);
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickSaveFileAsync(string suggestedName);
    Task<ConnectS3Request?> AskS3ConnectionAsync();
}

/// <summary>Operating-system integration.</summary>
public interface IShellService
{
    void OpenUrl(string url);
    void OpenFile(string path);
    Task CopyToClipboardAsync(string text);
}

/// <summary>Small app preferences (not secrets).</summary>
public interface IAppSettings
{
    string ServerUrl { get; set; }
    string DownloadFolder { get; set; }
    void Save();
}

/// <summary>
/// Receives an OAuth result on a random loopback port (RFC 8252): the API redirects the browser to
/// http://127.0.0.1:{port}/oauth/?status=...&amp;accountId=..., and we answer with a "you can close
/// this tab" page.
/// </summary>
public sealed class LoopbackOAuthReceiver : IDisposable
{
    private readonly HttpListener _listener = new();

    public LoopbackOAuthReceiver()
    {
        // Pick a free port, then listen on it (HttpListener needs an explicit prefix).
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        ReturnUrl = $"http://127.0.0.1:{port}/oauth/";
        _listener.Prefixes.Add(ReturnUrl);
        _listener.Start();
    }

    public string ReturnUrl { get; }

    /// <returns>The outcome status ("connected", "access_denied", ...) and the account id when connected.</returns>
    public async Task<(string Status, Guid? AccountId)> WaitAsync(CancellationToken ct)
    {
        await using var _ = ct.Register(() => _listener.Stop());
        var context = await _listener.GetContextAsync();
        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        var status = query["status"] ?? "unknown";

        var message = status == "connected" ? "Google Drive connected to FreeSpace." : $"Connection failed ({status}).";
        var page = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>FreeSpace</title><body style=\"font-family:sans-serif;padding:3em\"><h2>{WebUtility.HtmlEncode(message)}</h2><p>You can close this tab and return to the app.</p>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page, ct);
        context.Response.Close();

        return (status, Guid.TryParse(query["accountId"], out var id) ? id : null);
    }

    public void Dispose() => _listener.Close();
}

public static class Bytes
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long? bytes)
    {
        if (bytes is not { } value) return "—";
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value} B" : $"{size:0.#} {Units[unit]}";
    }
}
