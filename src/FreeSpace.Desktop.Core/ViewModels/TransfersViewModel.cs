using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpace.Contracts.Files;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Desktop.Core.Services;
using FreeSpace.Desktop.Core.Transfers;

namespace FreeSpace.Desktop.Core.ViewModels;

public enum TransferState
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}

public sealed partial class TransferItem : ObservableObject
{
    private readonly CancellationTokenSource _cancel = new();

    public TransferItem(string name, bool isUpload, long size)
    {
        Name = name;
        IsUpload = isUpload;
        Size = size;
    }

    public string Name { get; }
    public bool IsUpload { get; }
    public long Size { get; }
    public string Direction => IsUpload ? "↑" : "↓";
    public CancellationToken Token => _cancel.Token;

    [ObservableProperty] private TransferState _state;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _detail = "Waiting…";

    public bool IsActive => State is TransferState.Queued or TransferState.Running;

    partial void OnStateChanged(TransferState value) => OnPropertyChanged(nameof(IsActive));

    public void ReportBytes(long bytes)
    {
        Percent = Size == 0 ? 100 : Math.Min(100, 100.0 * bytes / Size);
        Detail = $"{Bytes.Format(bytes)} of {Bytes.Format(Size)}";
    }

    [RelayCommand]
    private void Cancel() => _cancel.Cancel();
}

/// <summary>
/// Upload/download queue shown at the bottom of the window. Runs two transfers at a time; each one
/// reports progress and can be cancelled. Failed uploads release their reserved space server-side.
/// </summary>
public sealed partial class TransfersViewModel(AppServices app) : ObservableObject
{
    private readonly SemaphoreSlim _slots = new(2, 2);

    public ObservableCollection<TransferItem> Items { get; } = [];

    [ObservableProperty] private bool _hasItems;

    public void EnqueueUpload(string path, Guid? parentId, Action<NodeResponse>? onCompleted = null)
    {
        var source = UploadSource.FromFile(path);
        var item = Add(new TransferItem(source.FileName, isUpload: true, source.Length));
        _ = RunAsync(item, async () =>
        {
            var engine = new UploadEngine(app.Api, app.ProviderHttp);
            var node = await engine.UploadAsync(source, parentId, new Progress<long>(item.ReportBytes), UploadRoute.Direct, item.Token);
            onCompleted?.Invoke(node);
        });
    }

    public void EnqueueDownload(NodeResponse node, string destination, bool openWhenDone)
    {
        var item = Add(new TransferItem(node.Name, isUpload: false, node.SizeBytes));
        _ = RunAsync(item, async () =>
        {
            await new DownloadEngine(app.Api).DownloadAsync(node.Id, node.SizeBytes, destination, new Progress<long>(item.ReportBytes), item.Token);
            if (openWhenDone) app.Shell.OpenFile(destination);
        });
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var done in Items.Where(i => !i.IsActive).ToList()) Items.Remove(done);
        HasItems = Items.Count > 0;
    }

    private TransferItem Add(TransferItem item)
    {
        Items.Insert(0, item);
        HasItems = true;
        return item;
    }

    private async Task RunAsync(TransferItem item, Func<Task> transfer)
    {
        try
        {
            await _slots.WaitAsync(item.Token);
        }
        catch (OperationCanceledException)
        {
            Finish(item, TransferState.Cancelled, "Cancelled");
            return;
        }

        try
        {
            item.State = TransferState.Running;
            item.Detail = "Starting…";
            await transfer();
            item.Percent = 100;
            Finish(item, TransferState.Done, item.IsUpload ? "Uploaded" : "Downloaded");
        }
        catch (OperationCanceledException)
        {
            Finish(item, TransferState.Cancelled, "Cancelled");
        }
        catch (ApiException e)
        {
            Finish(item, TransferState.Failed, e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Finish(item, TransferState.Failed, e.Message);
        }
        finally
        {
            _slots.Release();
        }
    }

    private static void Finish(TransferItem item, TransferState state, string detail)
    {
        item.State = state;
        item.Detail = detail;
    }
}
