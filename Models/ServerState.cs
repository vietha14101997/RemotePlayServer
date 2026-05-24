#nullable enable
using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemotePlayServer.Models;

public partial class ServerState : ObservableObject
{
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _localIp = "";
    [ObservableProperty] private int _port = 8288;
    [ObservableProperty] private string _encoder = "";
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private bool _adbReverseActive;
    [ObservableProperty] private string? _usbTetheringIp;
    [ObservableProperty] private string? _tunnelUrl;
    [ObservableProperty] private string? _authToken;
    [ObservableProperty] private bool _internetEnabled;
    [ObservableProperty] private string _preferredCodec = "Auto";
    [ObservableProperty] private ObservableCollection<MonitorInfo> _monitors = new();
    [ObservableProperty] private string _qrData = "";
    [ObservableProperty] private string _statusMessage = "Starting...";
    [ObservableProperty] private DateTime _startedAt;
    [ObservableProperty] private string? _guestId;
    [ObservableProperty] private string? _guestPassword;
    [ObservableProperty] private bool _isRelayConnected;
}
