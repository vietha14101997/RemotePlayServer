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
/// UDP broadcast beacon + probe responder for LAN auto-discovery.
///
/// Two mechanisms:
/// 1) BROADCAST: Periodically sends server info so clients can find the server passively.
/// 2) PROBE LISTENER: Listens for client discovery probe packets and replies directly.
///    This handles cases where broadcast doesn't cross subnets/VLANs.
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
    private Task? _listenerTask;
    private bool _disposed;

    public LanDiscoveryService(string serverIP, int serverPort, int discoveryPort = DefaultDiscoveryPort)
    {
        _serverIP = serverIP;
        _serverPort = serverPort;
        _discoveryPort = discoveryPort;
        _serverName = Environment.MachineName;
    }

    /// <summary>
    /// Start broadcasting beacon every interval + listening for client probes.
    /// </summary>
    public void Start(TimeSpan? interval = null)
    {
        if (_cts != null) return;

        var broadcastInterval = interval ?? TimeSpan.FromSeconds(2);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Start broadcast task (sends beacon periodically)
        _broadcastTask = Task.Run(() => BroadcastLoop(broadcastInterval, token), token);

        // Start probe listener task (responds to client discovery requests)
        _listenerTask = Task.Run(() => ProbeListenerLoop(token), token);
    }

    private async Task BroadcastLoop(TimeSpan interval, CancellationToken token)
    {
        var beacon = BuildBeacon();
        var beaconBytes = SysEncoding.UTF8.GetBytes(beacon);

        Logger.Info($"[Discovery] Broadcasting on UDP port {_discoveryPort} every {interval.TotalSeconds}s");

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
                Logger.Error($"[Discovery] Broadcast error: {ex.Message}");
            }

            try { await Task.Delay(interval, token); }
            catch (OperationCanceledException) { break; }
        }

        Logger.Info("[Discovery] Broadcast stopped");
    }

    /// <summary>
    /// Listen for client probe packets on the discovery port.
    /// When a probe is received, reply directly to the sender with beacon JSON.
    /// This bypasses broadcast routing issues (different subnets, WiFi isolation, etc).
    /// </summary>
    private async Task ProbeListenerLoop(CancellationToken token)
    {
        var beacon = BuildBeacon();
        var beaconBytes = SysEncoding.UTF8.GetBytes(beacon);

        Logger.Info($"[Discovery] Probe listener started on UDP port {_discoveryPort}");

        UdpClient? listener = null;
        try
        {
            listener = new UdpClient();
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var receiveTask = listener.ReceiveAsync();
                    var delayTask = Task.Delay(5000, token);

                    var completed = await Task.WhenAny(receiveTask, delayTask);
                    if (completed == delayTask || token.IsCancellationRequested)
                        continue;

                    var result = await receiveTask;
                    var message = SysEncoding.UTF8.GetString(result.Buffer);

                    // Check if this is a client probe (not our own broadcast)
                    if (message.Contains("RemotePlayClient") && message.Contains("discover"))
                    {
                        Logger.Info($"[Discovery] Probe received from {result.RemoteEndPoint} — replying with beacon");

                        // Reply directly to the sender's address and port
                        try
                        {
                            await listener.SendAsync(beaconBytes, beaconBytes.Length, result.RemoteEndPoint);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Discovery] Failed to reply to probe: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex)
                {
                    Logger.Error($"[Discovery] Listener socket error: {ex.Message}");
                    await Task.Delay(1000, token);
                }
                catch (ObjectDisposedException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"[Discovery] Probe listener error: {ex.Message}");
        }
        finally
        {
            try { listener?.Close(); } catch { }
            Logger.Info("[Discovery] Probe listener stopped");
        }
    }

    /// <summary>
    /// Stop broadcasting and listening.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        try { Task.WhenAll(_broadcastTask ?? Task.CompletedTask, _listenerTask ?? Task.CompletedTask).Wait(3000); }
        catch { }
        _cts?.Dispose();
        _cts = null;
        _broadcastTask = null;
        _listenerTask = null;
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
        return $"{{\"service\":\"RemotePlayServer\",\"ip\":\"{_serverIP}\",\"port\":\"{_serverPort}\",\"name\":\"{_serverName}\"}}";
    }
}
