#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using RemotePlayServer.Application.Protocol;

namespace RemotePlayServer.Models;

public partial class ClientConnectionInfo : ObservableObject
{
    public Guid ClientId { get; init; }
    [ObservableProperty] private string _remoteIp = "";
    [ObservableProperty] private ConnectionPhase _phase;
    [ObservableProperty] private string _codec = "";
    [ObservableProperty] private string _transportType = "";
    [ObservableProperty] private DateTime _connectedAt;
    [ObservableProperty] private bool _isUsbTransport;
    [ObservableProperty] private bool _isRelayTransport;
    [ObservableProperty] private string _iceConnectionType = ""; // "P2P" or "TURN Relay"
    [ObservableProperty] private TimeSpan _duration;
}
