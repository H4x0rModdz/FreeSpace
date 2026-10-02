using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using FreeSpace.Desktop.Core.ViewModels;
using FreeSpace.Desktop.Views;

namespace FreeSpace.Desktop;

/// <summary>Maps each page view model to its view (explicitly, so nothing depends on reflection or naming).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        LoginViewModel => new LoginView(),
        ShellViewModel => new ShellView(),
        FilesViewModel => new FilesView(),
        StorageViewModel => new StorageView(),
        TrashViewModel => new TrashView(),
        _ => new TextBlock { Text = $"No view for {param?.GetType().Name}" },
    };

    public bool Match(object? data) => data is ObservableObject;
}
