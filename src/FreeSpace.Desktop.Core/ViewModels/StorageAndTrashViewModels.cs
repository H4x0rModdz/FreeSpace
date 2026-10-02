using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpace.Contracts.Files;
using FreeSpace.Contracts.Storage;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Desktop.Core.Services;
using FreeSpace.Domain.Storage;

namespace FreeSpace.Desktop.Core.ViewModels;

public sealed class StorageAccountItem(StorageAccountResponse account)
{
    public StorageAccountResponse Account { get; } = account;
    public Guid Id => Account.Id;
    public string Name => Account.DisplayName;
    public string Detail => Account.Provider == StorageProvider.GoogleDrive
        ? $"Google Drive · {Account.Email}"
        : $"S3 · {Account.Config?.GetProperty("bucket").GetString()}";
    public string Usage => Account.TotalBytes is { } total
        ? $"{Bytes.Format(Account.UsedBytes)} of {Bytes.Format(total)} used"
        : $"{Bytes.Format(Account.UsedBytes)} used · no limit";
    public double UsedPercent => Account.TotalBytes is > 0 ? Math.Min(100, 100.0 * Account.UsedBytes / Account.TotalBytes.Value) : 0;
    public string StatusText => Account.Status switch
    {
        StorageAccountStatus.NeedsReauth => "Needs reconnecting",
        StorageAccountStatus.Disabled => "Disabled",
        _ => "Active",
    };
    public bool NeedsAttention => Account.Status == StorageAccountStatus.NeedsReauth;
    /// <summary>At 85% or more the usage bar switches to the warning color.</summary>
    public bool NearFull => UsedPercent >= 85;
}

/// <summary>Connected storage accounts: quota at a glance, connect Google Drive / S3, sync and remove.</summary>
public sealed partial class StorageViewModel(AppServices app) : ObservableObject
{
    public ObservableCollection<StorageAccountItem> Accounts { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Guard(async () =>
        {
            var accounts = await app.Api.StorageAccountsAsync();
            var summary = await app.Api.StorageSummaryAsync();
            Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(new StorageAccountItem(account));
            Summary = summary.ActiveAccounts == 0
                ? "No storage connected yet. Connect a Google Drive account or an S3 bucket to start uploading."
                : $"{Bytes.Format(summary.UsedBytes)} used of {Bytes.Format(summary.TotalBytes)}{(summary.HasUnlimitedAccount ? " + unlimited" : "")} across {summary.ActiveAccounts} account(s)";
        });
    }

    /// <summary>Opens Google consent in the browser and waits for the redirect on a loopback port.</summary>
    [RelayCommand]
    private async Task ConnectGoogleAsync()
    {
        await Guard(async () =>
        {
            IsBusy = true;
            try
            {
                using var receiver = new LoopbackOAuthReceiver();
                var authorization = await app.Api.AuthorizeGoogleAsync(receiver.ReturnUrl);
                app.Shell.OpenUrl(authorization.AuthorizationUrl);

                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var (status, _) = await receiver.WaitAsync(timeout.Token);
                if (status != "connected")
                    await app.Dialogs.ShowMessageAsync("Google Drive", $"The account was not connected ({status}).");
                await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
                // Gave up waiting for the browser.
            }
            finally
            {
                IsBusy = false;
            }
        });
    }

    [RelayCommand]
    private async Task ConnectS3Async()
    {
        var request = await app.Dialogs.AskS3ConnectionAsync();
        if (request is null) return;
        await Guard(async () =>
        {
            IsBusy = true;
            try
            {
                await app.Api.ConnectS3Async(request);
                await RefreshAsync();
            }
            finally
            {
                IsBusy = false;
            }
        });
    }

    [RelayCommand]
    private Task SyncAsync(StorageAccountItem item) => Guard(async () =>
    {
        await app.Api.SyncStorageAccountAsync(item.Id);
        await RefreshAsync();
    });

    [RelayCommand]
    private async Task RemoveAsync(StorageAccountItem item)
    {
        if (!await app.Dialogs.ConfirmAsync("Remove storage", $"Disconnect \"{item.Name}\" from FreeSpace? Files stored there must be deleted first.")) return;
        await Guard(async () =>
        {
            await app.Api.RemoveStorageAccountAsync(item.Id);
            await RefreshAsync();
        });
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            await app.Dialogs.ShowMessageAsync("Storage", e.Message);
        }
        catch (HttpRequestException)
        {
            await app.Dialogs.ShowMessageAsync("Connection problem", "Could not reach the server.");
        }
    }
}

public sealed class TrashItem(TrashItemResponse entry)
{
    public TrashItemResponse Entry { get; } = entry;
    public Guid Id => Entry.Node.Id;
    public string Name => Entry.Node.Name;
    public string GlyphData => new NodeItem(Entry.Node).GlyphData;
    public string Tint => new NodeItem(Entry.Node).Tint;
    public string Detail => Entry.ItemCount > 1 ? $"{Entry.ItemCount} items · trashed {Entry.TrashedAt.ToLocalTime():g}" : $"Trashed {Entry.TrashedAt.ToLocalTime():g}";
}

public sealed partial class TrashViewModel(AppServices app) : ObservableObject
{
    public ObservableCollection<TrashItem> Items { get; } = [];

    [ObservableProperty] private string? _status;

    [RelayCommand]
    private Task RefreshAsync() => Guard(async () =>
    {
        var entries = await app.Api.TrashListAsync(); // fetch first: a failed refresh must not wipe what is on screen
        Items.Clear();
        foreach (var entry in entries) Items.Add(new TrashItem(entry));
        Status = Items.Count == 0 ? "The trash is empty." : null;
    });

    [RelayCommand]
    private Task RestoreAsync(TrashItem item) => Guard(async () =>
    {
        await app.Api.RestoreAsync(item.Id);
        Items.Remove(item);
    });

    [RelayCommand]
    private async Task DeleteForeverAsync(TrashItem item)
    {
        if (!await app.Dialogs.ConfirmAsync("Delete forever", $"\"{item.Name}\" will be deleted permanently. This cannot be undone.")) return;
        await Guard(async () =>
        {
            await app.Api.DeleteForeverAsync(item.Id);
            Items.Remove(item);
        });
    }

    [RelayCommand]
    private async Task EmptyAsync()
    {
        if (Items.Count == 0 || !await app.Dialogs.ConfirmAsync("Empty trash", "Everything in the trash will be deleted permanently.")) return;
        await Guard(async () =>
        {
            await app.Api.EmptyTrashAsync();
            await RefreshAsync();
        });
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            await app.Dialogs.ShowMessageAsync("Trash", e.Message);
        }
        catch (HttpRequestException)
        {
            await app.Dialogs.ShowMessageAsync("Connection problem", "Could not reach the server.");
        }
    }
}
