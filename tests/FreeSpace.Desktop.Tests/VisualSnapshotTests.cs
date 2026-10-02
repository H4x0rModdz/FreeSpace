using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeSpace.Contracts.Files;
using FreeSpace.Contracts.Storage;
using FreeSpace.Contracts.Tenants;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Desktop.Core.Services;
using FreeSpace.Desktop.Core.ViewModels;
using FreeSpace.Desktop.Views;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;

namespace FreeSpace.Desktop.Tests;

/// <summary>
/// Renders each screen with sample data to PNG files, for design review. Runs only when
/// FREESPACE_SNAPSHOT_DIR is set (it writes files), so normal test runs stay side-effect free.
/// </summary>
public sealed class VisualSnapshotTests
{
    private static readonly string? Output = Environment.GetEnvironmentVariable("FREESPACE_SNAPSHOT_DIR");

    private sealed class NoopDialogs : IDialogService
    {
        public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<ConnectS3Request?> AskS3ConnectionAsync() => Task.FromResult<ConnectS3Request?>(null);
    }

    private sealed class NoopShell : IShellService
    {
        public void OpenUrl(string url) { }
        public void OpenFile(string path) { }
        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
    }

    private sealed class Settings : IAppSettings
    {
        public string ServerUrl { get; set; } = "";
        public string DownloadFolder { get; set; } = Path.GetTempPath();
        public void Save() { }
    }

    private static AppServices Services() => new(
        new FreeSpaceClient(new Uri("http://127.0.0.1:1/"), new InMemorySessionStore()), new NoopDialogs(), new NoopShell(), new Settings(), new HttpClient());

    private static AppViewModel App(object current)
    {
        var app = new AppViewModel(new InMemorySessionStore(), new NoopDialogs(), new NoopShell(), new Settings(), new HttpClient());
        app.Current = (CommunityToolkit.Mvvm.ComponentModel.ObservableObject)current;
        return app;
    }

    private static NodeItem Node(string name, NodeKind kind, long size = 0, string? mime = null, int daysAgo = 1) =>
        new(new NodeResponse(Guid.NewGuid(), null, kind, name, size, mime, DateTimeOffset.UtcNow.AddDays(-daysAgo), DateTimeOffset.UtcNow.AddDays(-daysAgo)));

    private static StorageAccountItem Account(string name, string email, long used, long total, StorageAccountStatus status = StorageAccountStatus.Active) =>
        new(new StorageAccountResponse(Guid.NewGuid(), StorageProvider.GoogleDrive, name, email, status, 0, total, used, total - used, DateTimeOffset.UtcNow, null,
            JsonDocument.Parse("{}").RootElement, DateTimeOffset.UtcNow));

    private static ShellViewModel SampleShell()
    {
        var shell = new ShellViewModel(Services());
        shell.Workspaces.Add(new TenantSummary(Guid.NewGuid(), "Admin's space", TenantRole.Owner, DateTimeOffset.UtcNow));
        shell.Workspace = shell.Workspaces[0];
        shell.UserName = "Admin";
        shell.UserEmail = "lobo@admin.com";

        foreach (var item in new[]
                 {
                     Node("Photos", NodeKind.Folder, daysAgo: 2), Node("Projects", NodeKind.Folder, daysAgo: 9), Node("Backups", NodeKind.Folder, daysAgo: 30),
                     Node("holiday-trip.mp4", NodeKind.File, 1_840_000_000, "video/mp4", 1), Node("annual-report.pdf", NodeKind.File, 4_200_000, "application/pdf", 3),
                     Node("moodboard.png", NodeKind.File, 8_900_000, "image/png", 4), Node("notes.txt", NodeKind.File, 2_300, "text/plain", 12),
                     Node("archive-2024.zip", NodeKind.File, 640_000_000, "application/zip", 40),
                 })
            shell.Files.Items.Add(item);
        shell.Files.Breadcrumbs.Add(new Breadcrumb(Guid.NewGuid(), "Projects"));

        var running = new TransferItem("holiday-trip.mp4", isUpload: true, 1_840_000_000) { State = TransferState.Running };
        running.ReportBytes(1_100_000_000);
        var done = new TransferItem("annual-report.pdf", isUpload: true, 4_200_000) { State = TransferState.Done, Detail = "Uploaded" };
        done.ReportBytes(4_200_000);
        shell.Transfers.Items.Add(running);
        shell.Transfers.Items.Add(done);
        shell.Transfers.HasItems = true;

        foreach (var account in new[]
                 {
                     Account("haniya tannai", "haniya@gmail.com", 11_400_000_000, 16_106_127_360),
                     Account("Lobo", "lobo@gmail.com", 4_100_000, 16_106_127_360),
                     Account("Work drive", "work@company.com", 15_900_000_000, 16_106_127_360, StorageAccountStatus.NeedsReauth),
                 })
            shell.Storage.Accounts.Add(account);
        shell.Storage.Summary = "27,3 GB used of 45 GB across 3 account(s)";

        foreach (var (item, count, daysAgo) in new[] { (Node("Old photos", NodeKind.Folder), 128, 2), (Node("draft-v2.pdf", NodeKind.File, 1_200_000, "application/pdf"), 1, 5) })
            shell.Trash.Items.Add(new TrashItem(new TrashItemResponse(item.Node, DateTimeOffset.UtcNow.AddDays(-daysAgo), null, count)));
        return shell;
    }

    private static void Snapshot(object content, string name)
    {
        var window = new MainWindow { Width = 1240, Height = 800, DataContext = App(content) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Directory.CreateDirectory(Output!);
        frame!.Save(Path.Combine(Output!, name + ".png"));
        window.Close();
    }

    [AvaloniaFact]
    public void Capture_screens()
    {
        if (Output is null) return;

        Snapshot(new LoginViewModel("http://localhost:18080/", (_, _) => Task.CompletedTask), "1-login");

        foreach (var (section, name) in new[] { (Section.Files, "2-files"), (Section.Storage, "3-storage"), (Section.Trash, "4-trash") })
        {
            var shell = SampleShell();
            shell.Section = section;
            Snapshot(shell, name);
        }
    }
}
