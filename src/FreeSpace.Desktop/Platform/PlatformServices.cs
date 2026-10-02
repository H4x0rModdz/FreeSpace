using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using FreeSpace.Contracts.Storage;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Desktop.Core.Services;

namespace FreeSpace.Desktop.Platform;

internal static class AppPaths
{
    public static string DataFolder
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeSpace");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

/// <summary>
/// Keeps the refresh token on disk, encrypted with Windows DPAPI for the current user. On other systems
/// the file is restricted to the user (0600), since there is no equivalent built-in secret store API.
/// </summary>
public sealed class ProtectedSessionStore : ISessionStore
{
    private static readonly byte[] Entropy = "FreeSpace.Desktop.Session.v1"u8.ToArray();
    private static string FilePath => Path.Combine(AppPaths.DataFolder, "session.bin");

    public StoredSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = File.ReadAllBytes(FilePath);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSession>(bytes);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            Clear(); // unreadable (another user/machine, corrupted): sign in again
            return null;
        }
    }

    public void Save(StoredSession session)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, bytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

public sealed class JsonAppSettings : IAppSettings
{
    private static string FilePath => Path.Combine(AppPaths.DataFolder, "settings.json");

    public string ServerUrl { get; set; } = "";
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FreeSpace");

    public static JsonAppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<JsonAppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public sealed class ShellService(Func<TopLevel> topLevel) : IShellService
{
    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public async Task CopyToClipboardAsync(string text)
    {
        if (topLevel().Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}

public sealed class DialogService(Func<Window> owner) : IDialogService
{
    public Task<string?> PromptAsync(string title, string message, string initialValue = "") =>
        Dialogs.PromptAsync(owner(), title, message, initialValue);

    public Task<bool> ConfirmAsync(string title, string message) => Dialogs.ConfirmAsync(owner(), title, message);

    public Task ShowMessageAsync(string title, string message) => Dialogs.MessageAsync(owner(), title, message);

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await owner().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Upload files", AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var file = await owner().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save as", SuggestedFileName = suggestedName });
        return file?.TryGetLocalPath();
    }

    public Task<ConnectS3Request?> AskS3ConnectionAsync() => Dialogs.S3ConnectionAsync(owner());
}
