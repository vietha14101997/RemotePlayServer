#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemotePlayServer.Application.Protocol;
using RemotePlayServer.Models;

namespace RemotePlayServer.ViewModels;

public partial class ConnectionManagerViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<ClientConnectionInfo> Clients { get; } = new();

    [ObservableProperty] private bool _hasClients;

    private readonly DispatcherTimer _refreshTimer;

    public ConnectionManagerViewModel()
    {
        PhaseProtocolHandler.OnClientConnected += OnClientConnected;
        PhaseProtocolHandler.OnClientDisconnected += OnClientDisconnected;

        foreach (var kvp in PhaseProtocolHandler.ActiveClients)
            Clients.Add(kvp.Value);
        HasClients = Clients.Count > 0;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += RefreshDurations;
        _refreshTimer.Start();
    }

    private void OnClientConnected(ClientConnectionInfo info)
    {
        Dispatch(() =>
        {
            Clients.Add(info);
            HasClients = true;
        });
    }

    private void OnClientDisconnected(Guid clientId)
    {
        Dispatch(() =>
        {
            for (int i = Clients.Count - 1; i >= 0; i--)
            {
                if (Clients[i].ClientId == clientId)
                {
                    Clients.RemoveAt(i);
                    break;
                }
            }
            HasClients = Clients.Count > 0;
        });
    }

    private void RefreshDurations(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        foreach (var c in Clients)
            c.Duration = now - c.ConnectedAt;
    }

    [RelayCommand]
    private void DisconnectClient(Guid clientId)
    {
        PhaseProtocolHandler.RequestDisconnect(clientId);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else
            action();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        PhaseProtocolHandler.OnClientConnected -= OnClientConnected;
        PhaseProtocolHandler.OnClientDisconnected -= OnClientDisconnected;
    }
}
