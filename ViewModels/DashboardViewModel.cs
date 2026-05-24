#nullable enable
using System;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemotePlayServer.Application.Protocol;
using RemotePlayServer.Models;
using RemotePlayServer.Server;
using RemotePlayServer.Services;

namespace RemotePlayServer.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ServerService _serverService;
    private readonly DispatcherTimer _uptimeTimer;
    private int _monitorRefreshCounter;

    public ServerState State => _serverService.State;

    [ObservableProperty] private ImageSource? _qrImage;
    [ObservableProperty] private int _activeClientCount;
    [ObservableProperty] private string _uptime = "00:00:00";

    public DashboardViewModel(ServerService serverService)
    {
        _serverService = serverService;
        _serverService.State.PropertyChanged += OnStatePropertyChanged;

        PhaseProtocolHandler.OnClientConnected += _ => Dispatch(() => ActiveClientCount = PhaseProtocolHandler.ActiveClients.Count);
        PhaseProtocolHandler.OnClientDisconnected += _ => Dispatch(() => ActiveClientCount = PhaseProtocolHandler.ActiveClients.Count);
        _activeClientCount = PhaseProtocolHandler.ActiveClients.Count;

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += OnTick;
        _uptimeTimer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var started = _serverService.State.StartedAt;
        if (started != default)
            Uptime = (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss");

        // Refresh monitors every 5 seconds
        if (++_monitorRefreshCounter >= 5)
        {
            _monitorRefreshCounter = 0;
            _serverService.RefreshMonitors();
        }
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerState.QrData))
            RegenerateQr();
    }

    private void RegenerateQr()
    {
        var data = _serverService.State.QrData;
        if (!string.IsNullOrEmpty(data))
            QrImage = QRCodeUtil.GenerateImageSource(data);
    }

    [RelayCommand]
    private void CopyQrData()
    {
        var data = _serverService.State.QrData;
        if (!string.IsNullOrEmpty(data))
            System.Windows.Clipboard.SetText(data);
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
