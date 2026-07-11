#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemotePlayServer.Application.Protocol;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Models;
using RemotePlayServer.Services;

namespace RemotePlayServer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ServerService _serverService;
    private readonly DashboardViewModel _dashboard;
    private readonly SettingsViewModel _settings;
    private readonly ConnectionManagerViewModel _connections;
    private readonly LogViewerViewModel _logViewer = new();
    private readonly LoginViewModel _login;
    private readonly DispatcherTimer _statusTimer;

    [ObservableProperty] private ObservableObject _currentView;
    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private bool _isSidebarExpanded = true;
    [ObservableProperty] private string _uptime = "00:00:00";
    [ObservableProperty] private int _activeClientCount;
    [ObservableProperty] private string _trayTooltip = "RemotePlay Server";

    public ServerState State => _serverService.State;

    public MainViewModel(ServerService serverService)
    {
        _serverService = serverService;
        _dashboard = new DashboardViewModel(serverService);
        _settings = new SettingsViewModel(serverService);
        _connections = new ConnectionManagerViewModel();
        _login = new LoginViewModel();
        _currentView = _dashboard; // start on dashboard

        _login.OnLoginSuccess += OnLoginSuccess;
        _login.OnLogoutRequested += OnLogout;

        PhaseProtocolHandler.OnClientConnected += _ => UpdateClientCount();
        PhaseProtocolHandler.OnClientDisconnected += _ => UpdateClientCount();
        _activeClientCount = PhaseProtocolHandler.ActiveClients.Count;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();
    }

    /// <summary>
    /// Load saved credentials into login form + try auto-login.
    /// Call from App.xaml.cs after startup completes.
    /// </summary>
    public async Task TryAutoLoginAsync()
    {
        var config = _serverService.GetInternetConfig();
        if (config != null)
            Dispatch(() => _login.LoadFromConfig(config));

        if (config is not { UseRelay: true } ||
            string.IsNullOrEmpty(config.RelayEmail) ||
            string.IsNullOrEmpty(config.RelayPassword))
        {
            // Without relay login there is no room, so the QR stays tunnel-only.
            // Say so loudly — a hand-edited internet-settings.json losing one of
            // these fields previously failed silently and looked like a QR bug.
            RemotePlayServer.Core.Logger.Warn(
                "[Relay] Auto-login skipped: useRelay/relayEmail/relayPassword incomplete in internet-settings.json — QR will NOT carry relay room credentials");
            return;
        }

        var client = new RelayClient();
        var success = await client.LoginAsync(config.RelayUrl!, config.RelayEmail!, config.RelayPassword!);
        if (success)
        {
            Dispatch(() => _login.SetLoggedIn(config.RelayEmail!));
            _ = Task.Run(() => _serverService.ConnectRelayAsync(client));
        }
        else
        {
            client.Dispose();
        }
    }

    private void OnLoginSuccess(RelayClient client)
    {
        // Save credentials if remember me
        if (_login.RememberMe)
        {
            _serverService.SaveRelayCredentials(
                _login.RelayUrl.Trim(),
                _login.Email.Trim(),
                _login.Password);
        }

        // Start relay connection with the authenticated client
        _ = Task.Run(() => _serverService.ConnectRelayAsync(client));
    }

    public void OnLogout()
    {
        RelayClientManager.Shutdown();
        Dispatch(() =>
        {
            State.IsRelayConnected = false;
            State.GuestId = null;
            State.GuestPassword = null;
        });
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        CurrentView = value switch
        {
            0 => _dashboard,
            1 => _settings,
            2 => _connections,
            3 => _logViewer,
            4 => _login,
            _ => _dashboard
        };
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    [RelayCommand]
    private void ShowWindow()
    {
        var window = System.Windows.Application.Current.MainWindow;
        if (window != null)
        {
            window.Show();
            window.WindowState = System.Windows.WindowState.Normal;
            window.Activate();
        }
    }

    [RelayCommand]
    private void ExitApp() => System.Windows.Application.Current.Shutdown();

    private void OnStatusTick(object? sender, EventArgs e)
    {
        var started = _serverService.State.StartedAt;
        if (started != default)
            Uptime = (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss");

        var status = _serverService.State.IsRunning ? "Running" : "Stopped";
        TrayTooltip = $"RemotePlay Server • {status} • {ActiveClientCount} client(s)";
    }

    private void UpdateClientCount()
    {
        Dispatch(() => ActiveClientCount = PhaseProtocolHandler.ActiveClients.Count);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else
            action();
    }
}
