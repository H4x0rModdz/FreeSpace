using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpace.Contracts.Tenants;
using FreeSpace.Desktop.Core.Api;
using FreeSpace.Desktop.Core.Services;

namespace FreeSpace.Desktop.Core.ViewModels;

/// <summary>Everything a signed-in screen needs: the API client plus platform services.</summary>
public sealed record AppServices(FreeSpaceClient Api, IDialogService Dialogs, IShellService Shell, IAppSettings Settings, HttpClient ProviderHttp);

/// <summary>Top of the app: shows the login screen or the signed-in shell.</summary>
public sealed partial class AppViewModel : ObservableObject
{
    private readonly ISessionStore _sessions;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IAppSettings _settings;
    private readonly HttpClient _providerHttp;
    private readonly Func<Uri, FreeSpaceClient> _clientFactory;

    [ObservableProperty] private ObservableObject? _current;

    public AppViewModel(ISessionStore sessions, IDialogService dialogs, IShellService shell, IAppSettings settings, HttpClient providerHttp,
        Func<Uri, FreeSpaceClient>? clientFactory = null)
    {
        _sessions = sessions;
        _dialogs = dialogs;
        _shell = shell;
        _settings = settings;
        _providerHttp = providerHttp;
        _clientFactory = clientFactory ?? (url => new FreeSpaceClient(url, sessions));
        ShowLogin();
    }

    /// <summary>Resumes the previous session, if the saved refresh token still works.</summary>
    public async Task StartAsync()
    {
        if (!Uri.TryCreate(_settings.ServerUrl, UriKind.Absolute, out var url)) return;
        var client = _clientFactory(url);
        try
        {
            if (await client.TryResumeAsync()) await EnterAsync(client);
            else client.Dispose();
        }
        catch (HttpRequestException)
        {
            client.Dispose(); // server unreachable: stay on the login screen
        }
    }

    private void ShowLogin() => Current = new LoginViewModel(_settings.ServerUrl, async (url, signIn) =>
    {
        var client = _clientFactory(url);
        try
        {
            await signIn(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _settings.ServerUrl = url.ToString();
        _settings.Save();
        await EnterAsync(client);
    });

    private async Task EnterAsync(FreeSpaceClient client)
    {
        client.SessionEnded += (_, _) => ShowLogin();
        var shell = new ShellViewModel(new AppServices(client, _dialogs, _shell, _settings, _providerHttp));
        await shell.LoadAsync();
        Current = shell;
    }
}

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly Func<Uri, Func<FreeSpaceClient, Task>, Task> _signIn;

    public LoginViewModel(string serverUrl, Func<Uri, Func<FreeSpaceClient, Task>, Task> signIn)
    {
        _serverUrl = string.IsNullOrWhiteSpace(serverUrl) ? "http://localhost:8080/" : serverUrl;
        _signIn = signIn;
    }

    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isRegistering;
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private bool _isBusy;

    public string Title => IsRegistering ? "Create your FreeSpace account" : "Sign in to FreeSpace";
    public string SubmitText => IsRegistering ? "Create account" : "Sign in";
    public string ToggleText => IsRegistering ? "I already have an account" : "Create an account";

    partial void OnIsRegisteringChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(ToggleText));
        Error = null;
    }

    [RelayCommand]
    private void Toggle() => IsRegistering = !IsRegistering;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        Error = null;
        if (!Uri.TryCreate(ServerUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            Error = "Enter the server address, e.g. https://freespace.example.com";
            return;
        }

        IsBusy = true;
        try
        {
            await _signIn(url, client => IsRegistering
                ? client.RegisterAsync(Name.Trim(), Email.Trim(), Password)
                : client.LoginAsync(Email.Trim(), Password));
        }
        catch (ApiException e)
        {
            Error = e.Message;
        }
        catch (HttpRequestException)
        {
            Error = "Could not reach the server. Check the address and your connection.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSubmit() => !IsBusy;
}

public enum Section
{
    Files,
    Storage,
    Trash,
}

/// <summary>The signed-in window: sections, workspace switcher and the transfers panel.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppServices _app;
    private bool _switching;

    public ShellViewModel(AppServices app)
    {
        _app = app;
        Transfers = new TransfersViewModel(app);
        Files = new FilesViewModel(app, Transfers);
        Storage = new StorageViewModel(app);
        Trash = new TrashViewModel(app);
        _currentPage = Files;
    }

    public FilesViewModel Files { get; }
    public StorageViewModel Storage { get; }
    public TrashViewModel Trash { get; }
    public TransfersViewModel Transfers { get; }

    public ObservableCollection<TenantSummary> Workspaces { get; } = [];

    [ObservableProperty] private ObservableObject _currentPage;
    [ObservableProperty] private Section _section;
    [ObservableProperty] private TenantSummary? _workspace;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _userEmail = "";

    public async Task LoadAsync()
    {
        var me = await _app.Api.MeAsync();
        UserName = me.User.Name;
        UserEmail = me.User.Email;

        _switching = true;
        Workspaces.Clear();
        foreach (var tenant in await _app.Api.TenantsAsync()) Workspaces.Add(tenant);
        Workspace = Workspaces.FirstOrDefault(w => w.Id == me.Tenant.Id);
        _switching = false;

        await Files.LoadAsync();
    }

    partial void OnSectionChanged(Section value)
    {
        CurrentPage = value switch
        {
            Section.Storage => Storage,
            Section.Trash => Trash,
            _ => Files,
        };
        _ = value switch
        {
            Section.Storage => Storage.RefreshCommand.ExecuteAsync(null),
            Section.Trash => Trash.RefreshCommand.ExecuteAsync(null),
            _ => Files.RefreshCommand.ExecuteAsync(null),
        };
    }

    partial void OnWorkspaceChanged(TenantSummary? value)
    {
        if (_switching || value is null) return;
        _ = SwitchAsync(value);
    }

    private async Task SwitchAsync(TenantSummary workspace)
    {
        try
        {
            await _app.Api.SwitchTenantAsync(workspace.Id);
            await Files.GoToRootAsync();
            Section = Section.Files;
        }
        catch (ApiException e)
        {
            await _app.Dialogs.ShowMessageAsync("Could not switch workspace", e.Message);
        }
    }

    [RelayCommand]
    private void Navigate(Section section) => Section = section;

    [RelayCommand]
    private Task LogoutAsync() => _app.Api.LogoutAsync();
}
