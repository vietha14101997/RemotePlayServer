#nullable enable
using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemotePlayServer.Application.Protocol;
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
        _currentView = _dashboard;

        PhaseProtocolHandler.OnClientConnected += _ => UpdateClientCount();
        PhaseProtocolHandler.OnClientDisconnected += _ => UpdateClientCount();
        _activeClientCount = PhaseProtocolHandler.ActiveClients.Count;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        CurrentView = value switch
        {
            0 => _dashboard,
            1 => _settings,
            2 => _connections,
            3 => _logViewer,
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
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => ActiveClientCount = PhaseProtocolHandler.ActiveClients.Count);
        else
            ActiveClientCount = PhaseProtocolHandler.ActiveClients.Count;
    }
}
