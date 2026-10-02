using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpace.Contracts.Files;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Desktop.Core.Services;
using FreeSpace.Domain.Files;

namespace FreeSpace.Desktop.Core.ViewModels;

public sealed class NodeItem(NodeResponse node)
{
    public NodeResponse Node { get; } = node;
    public Guid Id => Node.Id;
    public string Name => Node.Name;
    public bool IsFolder => Node.Kind == NodeKind.Folder;
    /// <summary>Vector glyph (24x24 box) and tint of the file tile; the view draws a glossy gel tile from them.</summary>
    public string GlyphData => FileGlyphs.For(Node.Kind, Node.MimeType).Glyph;
    public string Tint => FileGlyphs.For(Node.Kind, Node.MimeType).Tint;
    public string Size => IsFolder ? "" : Bytes.Format(Node.SizeBytes);
    public string Modified => Node.UpdatedAt.ToLocalTime().ToString("g");
}

public sealed record Breadcrumb(Guid? Id, string Name);

/// <summary>Browses the virtual tree: navigation, search, folder operations, uploads and downloads.</summary>
public sealed partial class FilesViewModel(AppServices app, TransfersViewModel transfers) : ObservableObject
{
    public ObservableCollection<NodeItem> Items { get; } = [];
    public ObservableCollection<Breadcrumb> Breadcrumbs { get; } = [new(null, "My files")];

    [ObservableProperty] private Guid? _folderId;
    [ObservableProperty] private NodeItem? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _status;

    public Task LoadAsync() => RefreshAsync();

    public Task GoToRootAsync() => NavigateToAsync(Breadcrumbs[0]);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Guard(async () =>
        {
            IsLoading = true;
            try
            {
                var nodes = IsSearching && SearchText.Length > 0
                    ? await app.Api.SearchAsync(SearchText.Trim())
                    : await app.Api.ListAllAsync(FolderId);
                Items.Clear();
                foreach (var node in nodes) Items.Add(new NodeItem(node));
                Status = Items.Count == 0 ? (IsSearching ? "No matches." : "This folder is empty. Drop files here to upload.") : null;
            }
            finally
            {
                IsLoading = false;
            }
        });
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsSearching = SearchText.Trim().Length > 0;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchText = "";
        IsSearching = false;
        await RefreshAsync();
    }

    /// <summary>Double-click: folders open, files download to the default folder and open.</summary>
    [RelayCommand]
    private async Task OpenAsync(NodeItem? item)
    {
        if (item is null) return;
        if (!item.IsFolder)
        {
            await DownloadAndOpenAsync(item);
            return;
        }

        IsSearching = false;
        SearchText = "";
        var details = await app.Api.NodeAsync(item.Id);
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Breadcrumb(null, "My files"));
        foreach (var segment in details.Path) Breadcrumbs.Add(new Breadcrumb(segment.Id, segment.Name));
        FolderId = item.Id;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task NavigateToAsync(Breadcrumb crumb)
    {
        var index = Breadcrumbs.IndexOf(crumb);
        while (index >= 0 && Breadcrumbs.Count > index + 1) Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        FolderId = crumb.Id;
        IsSearching = false;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UpAsync()
    {
        if (Breadcrumbs.Count > 1) await NavigateToAsync(Breadcrumbs[^2]);
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var name = await app.Dialogs.PromptAsync("New folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Guard(async () =>
        {
            await app.Api.CreateFolderAsync(FolderId, name.Trim());
            await RefreshAsync();
        });
    }

    [RelayCommand]
    private async Task RenameAsync(NodeItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        var name = await app.Dialogs.PromptAsync("Rename", "New name:", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        await Guard(async () =>
        {
            await app.Api.RenameAsync(item.Id, name.Trim());
            await RefreshAsync();
        });
    }

    [RelayCommand]
    private async Task TrashAsync(NodeItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        await Guard(async () =>
        {
            await app.Api.TrashAsync(item.Id);
            Items.Remove(item);
        });
    }

    [RelayCommand]
    private async Task ShareAsync(NodeItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        await Guard(async () =>
        {
            var share = await app.Api.ShareAsync(item.Id, expiresAt: null);
            var link = share.Url ?? new Uri(app.Api.ServerUrl, $"api/v1/public/shares/{share.Token}").ToString();
            await app.Shell.CopyToClipboardAsync(link);
            await app.Dialogs.ShowMessageAsync("Link copied", $"Anyone with this link can open \"{item.Name}\":\n\n{link}");
        });
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        var files = await app.Dialogs.PickFilesAsync();
        UploadPaths(files);
    }

    /// <summary>Queues files (from the picker or drag and drop) for upload into the current folder.</summary>
    public void UploadPaths(IEnumerable<string> paths)
    {
        var target = FolderId;
        foreach (var path in paths.Where(File.Exists))
            transfers.EnqueueUpload(path, target, onCompleted: node => { if (FolderId == target && !IsSearching) Items.Add(new NodeItem(node)); });
    }

    [RelayCommand]
    private async Task DownloadAsync(NodeItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        if (item.IsFolder)
        {
            await app.Dialogs.ShowMessageAsync("Download folder", "Folder downloads are not available in the app yet.");
            return;
        }
        var destination = await app.Dialogs.PickSaveFileAsync(item.Name);
        if (destination is not null) transfers.EnqueueDownload(item.Node, destination, openWhenDone: false);
    }

    private Task DownloadAndOpenAsync(NodeItem item)
    {
        var folder = app.Settings.DownloadFolder;
        Directory.CreateDirectory(folder);
        transfers.EnqueueDownload(item.Node, UniquePath(Path.Combine(folder, item.Name)), openWhenDone: true);
        return Task.CompletedTask;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var (dir, stem, ext) = (Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            await app.Dialogs.ShowMessageAsync("Something went wrong", e.Message);
        }
        catch (HttpRequestException)
        {
            await app.Dialogs.ShowMessageAsync("Connection problem", "Could not reach the server.");
        }
    }
}

/// <summary>Maps a file type to a simple vector glyph and a tile color (the UI draws the glossy tile).</summary>
public static class FileGlyphs
{
    private const string Folder = "M3 7a2 2 0 0 1 2-2h4.2l2 2H19a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z";
    private const string Play = "M8.5 6.5v11l9-5.5z";
    private const string Image = "M4 18l5.2-7 3.6 4.6 2.2-2.8L20 18z M16.5 6.5a1.8 1.8 0 1 1 0 3.6a1.8 1.8 0 0 1 0-3.6z";
    private const string Note = "M10 6v8.2A3 3 0 1 0 12 17V9h5V6z";
    private const string Lines = "M7 7h10v2H7z M7 11h10v2H7z M7 15h6v2H7z";
    private const string Archive = "M5 6h14v3.2H5z M6.2 10.5h11.6V18H6.2z";
    private const string Page = "M7 4h6.5L18 8.5V20H7z";

    public static (string Glyph, string Tint) For(NodeKind kind, string? mimeType) => kind == NodeKind.Folder
        ? (Folder, "#FFB347")
        : mimeType switch
        {
            null => (Page, "#A5A3C9"),
            _ when mimeType.StartsWith("video/") => (Play, "#9A7BFF"),
            _ when mimeType.StartsWith("image/") => (Image, "#FF7AC6"),
            _ when mimeType.StartsWith("audio/") => (Note, "#7ED957"),
            "application/pdf" => (Lines, "#FF6B7A"),
            _ when mimeType.Contains("zip") || mimeType.Contains("compressed") => (Archive, "#2CC7B4"),
            _ when mimeType.StartsWith("text/") => (Lines, "#5AA9FF"),
            _ => (Page, "#A5A3C9"),
        };
}
