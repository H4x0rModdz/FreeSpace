using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FreeSpace.Desktop.Core.ViewModels;
using FreeSpace.Desktop.Platform;
using FreeSpace.Desktop.Views;

namespace FreeSpace.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            // Talks to Drive/S3 directly for uploads; separate from the API client so its timeouts and auth never mix.
            var providerHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var viewModel = new AppViewModel(
                new ProtectedSessionStore(),
                new DialogService(() => window),
                new ShellService(() => window),
                JsonAppSettings.Load(),
                providerHttp);

            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => providerHttp.Dispose();
            _ = viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
