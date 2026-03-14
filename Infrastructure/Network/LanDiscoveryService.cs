#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using SysEncoding = System.Text.Encoding;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// UDP broadcast beacon for LAN auto-discovery.
/// Periodically broadcasts server info so clients can find the server without manual IP entry or QR scanning.
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
    public const int DefaultDiscoveryPort = 8289;

    private readonly int _discoveryPort;
    private readonly int _serverPort;
    private readonly string _serverName;
    private readonly string _serverIP;
    private CancellationTokenSource? _cts;
    private Task? _broadcastTask;
    private bool _disposed;

    public LanDiscoveryService(string serverIP, int serverPort, int discoveryPort = DefaultDiscoveryPort)
    {
        _serverIP = serverIP;
        _serverPort = serverPort;
        _discoveryPort = discoveryPort;
        _serverName = Environment.MachineName;
    }

    /// <summary>
    /// Start broadcasting beacon every interval.
    /// </summary>
    public void Start(TimeSpan? interval = null)
    {
        if (_cts != null) return;

        var broadcastInterval = interval ?? TimeSpan.FromSeconds(2);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _broadcastTask = Task.Run(async () =>
        {
            // Build beacon payload once (static data)
            var beacon = BuildBeacon();
            var beaconBytes = SysEncoding.UTF8.GetBytes(beacon);

            Logger.Info($"[Discovery] Broadcasting on UDP port {_discoveryPort} every {broadcastInterval.TotalSeconds}s");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var udp = new UdpClient();
                    udp.EnableBroadcast = true;
                    var endpoint = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);
                    await udp.SendAsync(beaconBytes, beaconBytes.Length, endpoint);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Transient network errors — log but continue
                    Logger.Error($"[Discovery] Broadcast error: {ex.Message}");
                }

                try { await Task.Delay(broadcastInterval, token); }
                catch (OperationCanceledException) { break; }
            }

            Logger.Info("[Discovery] Broadcast stopped");
        }, token);
    }

    /// <summary>
    /// Stop broadcasting.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        try { _broadcastTask?.Wait(3000); } catch { }
        _cts?.Dispose();
        _cts = null;
        _broadcastTask = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private string BuildBeacon()
    {
        // Keep compatible with QRScannerConfig format on client side
        // Client reuses QRScannerConfig.FromJson() to parse this
        return $"{{\"service\":\"RemotePlayServer\",\"ip\":\"{_serverIP}\",\"port\":\"{_serverPort}\",\"name\":\"{_serverName}\"}}";
    }
}
