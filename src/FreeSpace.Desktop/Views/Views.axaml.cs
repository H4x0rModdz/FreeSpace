using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FreeSpace.Desktop.Core.ViewModels;

namespace FreeSpace.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class LoginView : UserControl
{
    public LoginView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class ShellView : UserControl
{
    public ShellView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class StorageView : UserControl
{
    public StorageView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class TrashView : UserControl
{
    public TrashView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class FilesView : UserControl
{
    public FilesView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private FilesViewModel? ViewModel => DataContext as FilesViewModel;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: NodeItem item }) ViewModel?.OpenCommand.Execute(item);
    }

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    /// <summary>Dropped files upload into the folder being shown.</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths is { Count: > 0 }) ViewModel?.UploadPaths(paths);
        e.Handled = true;
    }
}
