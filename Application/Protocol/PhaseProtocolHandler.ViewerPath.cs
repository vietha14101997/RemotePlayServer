#nullable enable
using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Application.Protocol;

public partial class PhaseProtocolHandler
{
    /// <summary>
    /// Viewer fast-path: skip capture/encode, only setup WebRTC + receive pre-encoded frames.
    /// Host encodes once → SharedEncoderManager fans out → viewer's DCs send to client.
    /// </summary>
    private async Task RunViewerFastPathAsync()
    {
        Logger.Info("[Protocol] Viewer fast-path: skipping Phase 1+2 config, using host encoder");

        // 1. Wait for host encoder to be available
        var mgr = SharedEncoderManager.Instance;
        if (mgr == null || !mgr.HasHost)
        {
            Logger.Info("[Protocol] Viewer: waiting for host encoder...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                mgr = SharedEncoderManager.Instance;
                if (mgr != null && mgr.HasHost) break;
                await Task.Delay(500, _ct);
            }

            if (mgr == null || !mgr.HasHost)
            {
                Logger.Error("[Protocol] Viewer: host encoder not available, disconnecting");
                await SendErrorAsync(2, "NO_HOST", "Host is not streaming");
                return;
            }
        }

        // 2. Get host's stream config
        var hostConfig = mgr.GetHostConfig();
        if (hostConfig == null)
        {
            Logger.Error("[Protocol] Viewer: host config not available");
            await SendErrorAsync(2, "NO_CONFIG", "Host config not available");
            return;
        }

        int actualMonitors = hostConfig.MonitorCount;
        var negotiatedCodec = hostConfig.Codec;
        int resolutionHeight = hostConfig.ResolutionHeight;
        int fps = hostConfig.Fps;

        Logger.Info($"[Protocol] Viewer using host config: {actualMonitors} monitors, " +
                   $"{negotiatedCodec}, {resolutionHeight}p@{fps}fps");

        // 3. Use host's monitor list
        _monitors = Infrastructure.Capture.WgcInterop.ListMonitorsDXGI()
            .Select(m => (m.hmon, m.name, m.width, m.height)).ToList();

        // 7. Create streamer (WebRTC PeerConnection + DCs) — NO capture, NO encoder
        _allConnectedTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _streamer = new SIPSorceryStreamer(
            actualMonitors, fps, resolutionHeight,
            mgr.GetHostDevice(),
            negotiatedCodec);
        // Opaque session id for the runtime-truth telemetry snapshots (contract-v1).
        _streamer.SessionId = _clientId.ToString();

        // Wire ICE candidate forwarding + input handling (same as host)
        _streamer.OnIceCandidate += async (candidate) =>
        {
            try
            {
                if (_ws.State != WebSocketState.Open) return;
                var msg = new CandidateMessage { MonitorIndex = 0, Candidate = candidate };
                await SendMessageAsync(msg);
            }
            catch { }
        };

        _streamer.OnInputReceived += (data) =>
        {
            InputReceiver.HandleInputMessage(data,
                _monitors.Select(m => (0, 0, m.width, m.height)).ToList());
        };

        _streamer.OnAllTracksReady += async () =>
        {
            try
            {
                _allConnectedTcs?.TrySetResult(true);
                OnStreamerReady?.Invoke(_streamer);

                if (_activeClients.TryGetValue(_clientId, out var info))
                {
                    info.IceConnectionType = _streamer?.DetectIceConnectionType() ?? "Unknown";
                    info.IsRelayTransport = info.IceConnectionType == "TURN Relay";
                }

                Logger.Info("[Protocol] Viewer: all tracks ready, sending ice_ready");
                await SendTextAsync("{\"type\":\"ice_ready\"}");
            }
            catch { }
        };

        _streamer.OnFatalError += (msg) =>
        {
            Logger.Error($"[Protocol] Viewer fatal: {msg}");
        };

        // 8. Send config_complete (includes ephemeral TURN credentials for the client's PCs)
        var sessionIce = TurnCredentialProvider.GetSessionIceServers();
        var completeMsg = new ConfigCompleteMessage
        {
            Monitors = _monitors.Select((m, i) => new MonitorInfoDto
            {
                Id = i, Name = m.name, Width = m.width, Height = m.height
            }).Take(actualMonitors).ToList(),
            CaptureReady = true,
            IceServers = sessionIce.Count > 0 ? sessionIce : null
        };
        await SendMessageAsync(completeMsg);

        // 9. ICE exchange
        SetPhase(ConnectionPhase.Phase2_IceExchange);
        await RunIceExchangeAsync();
        Logger.Info("[Protocol] Viewer ICE exchange complete");

        // 10. Wait for all DCs to connect
        SetPhase(ConnectionPhase.Phase3_Streaming);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await _allConnectedTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("[Protocol] Viewer: DC connect timeout, continuing anyway");
        }

        Logger.Info("[Protocol] Viewer streaming active — receiving frames from host encoder");

        // 11. Viewer message loop (keepalive + control only, no capture)
        await RunViewerMessageLoopAsync();
    }

    /// <summary>
    /// Viewer message loop: keepalive, ICE reconnect, disconnect handling.
    /// No capture, no encode, no quality/fps adjustments.
    /// </summary>
    private async Task RunViewerMessageLoopAsync()
    {
        StartKeepAlive();

        try
        {
            var buffer = new byte[64 * 1024];
            while (!_ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Binary)
                    continue; // Viewer doesn't send binary

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);

                    // Handle ping/pong
                    if (text == "pong" || text.StartsWith("pong:"))
                    {
                        _lastPongReceived = DateTime.UtcNow;
                        _missedPongs = 0;
                        continue;
                    }

                    // Handle viewer quality change
                    if (text.Contains("\"type\":\"set_quality\""))
                    {
                        try
                        {
                            var doc = System.Text.Json.JsonDocument.Parse(text);
                            var preset = doc.RootElement.GetProperty("quality").GetString();
                            var quality = preset switch
                            {
                                "low" => ViewerQuality.Low,
                                "medium" => ViewerQuality.Medium,
                                _ => ViewerQuality.High
                            };
                            SharedEncoderManager.Instance?.SetViewerQuality(_clientId, quality);
                            Logger.Info($"[Protocol] Viewer quality changed to {quality}");
                        }
                        catch { }
                        continue;
                    }

                    // Handle ICE candidates during streaming
                    if (text.Contains("\"type\":\"candidate\"") || text.Contains("\"type\":\"video_candidate\""))
                    {
                        Logger.Debug("[Protocol] Viewer: late ICE candidate received");
                        continue;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            StopKeepAlive();
            Logger.Info("[Protocol] Viewer message loop ended");
        }
    }
}
