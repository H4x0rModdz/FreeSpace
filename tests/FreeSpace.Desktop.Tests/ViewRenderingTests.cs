using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeSpace.Contracts.Storage;
using FreeSpace.Desktop;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Desktop.Core.Services;
using FreeSpace.Desktop.Core.ViewModels;
using FreeSpace.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(FreeSpace.Desktop.Tests.TestAppBuilder))]

namespace FreeSpace.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia() // real rendering, so snapshots show what the user sees
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Loads every screen with its view model in a headless window: catches XAML/runtime binding problems
/// that compiled bindings cannot (resources, templates, view lookup).
/// </summary>
public sealed class ViewRenderingTests
{
    private sealed class FakeDialogs : IDialogService
    {
        public List<string> Messages { get; } = [];
        public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<ConnectS3Request?> AskS3ConnectionAsync() => Task.FromResult<ConnectS3Request?>(null);
    }

    private sealed class FakeShell : IShellService
    {
        public void OpenUrl(string url) { }
        public void OpenFile(string path) { }
        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
    }

    private sealed class FakeSettings : IAppSettings
    {
        public string ServerUrl { get; set; } = "";
        public string DownloadFolder { get; set; } = Path.GetTempPath();
        public void Save() { }
    }

    /// <summary>A client pointing at a closed port: screens render, API calls fail gracefully.</summary>
    private static AppServices OfflineContext(FakeDialogs dialogs) =>
        new(new FreeSpaceClient(new Uri("http://127.0.0.1:1/"), new InMemorySessionStore()), dialogs, new FakeShell(), new FakeSettings(), new HttpClient());

    private static Window Show(Control content)
    {
        var window = new Window { Width = 1180, Height = 760, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Login_screen_renders_and_switches_to_registration()
    {
        var viewModel = new LoginViewModel("", (_, _) => Task.CompletedTask);
        var window = Show(new LoginView { DataContext = viewModel });

        viewModel.ToggleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var visibleInputs = window.GetVisualDescendants().OfType<TextBox>().Count(t => t.IsEffectivelyVisible);
        Assert.Equal(4, visibleInputs); // server, name, e-mail, password
        Assert.Equal("Create account", viewModel.SubmitText);
    }

    [AvaloniaFact]
    public void Shell_renders_every_section_through_the_view_locator()
    {
        var dialogs = new FakeDialogs();
        var shell = new ShellViewModel(OfflineContext(dialogs));
        var window = Show(new ShellView { DataContext = shell });

        Assert.Single(window.GetVisualDescendants().OfType<FilesView>());

        shell.NavigateCommand.Execute(Section.Storage);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.GetVisualDescendants().OfType<StorageView>());

        shell.NavigateCommand.Execute(Section.Trash);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.GetVisualDescendants().OfType<TrashView>());
    }

    [AvaloniaFact]
    public void Transfers_panel_appears_with_queued_items()
    {
        var shell = new ShellViewModel(OfflineContext(new FakeDialogs()));
        var window = Show(new ShellView { DataContext = shell });
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, new byte[16]);
            shell.Transfers.EnqueueUpload(file, parentId: null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(shell.Transfers.HasItems);
            Assert.Contains(window.GetVisualDescendants().OfType<ProgressBar>(), p => p.IsEffectivelyVisible);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
