#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using RemotePlayServer.Configuration;
using RemotePlayServer.Application.Security;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol
{
    public partial class PhaseProtocolHandler
    {
        private void SetPhase(ConnectionPhase phase)
        {
            _phase = phase;
            Logger.Info($"[Protocol] Phase changed to: {phase}");

            if (_activeClients.TryGetValue(_clientId, out var ci))
                ci.Phase = phase;
            OnClientPhaseChanged?.Invoke(_clientId, phase);
        }

        private int GetPhaseNumber()
        {
            return _phase switch
            {
                ConnectionPhase.Phase1_HardwareDetect or ConnectionPhase.Phase1_SpeedTest or ConnectionPhase.Phase1_WaitingProceed => 1,
                ConnectionPhase.Phase2_ApplyConfig or ConnectionPhase.Phase2_IceExchange => 2,
                ConnectionPhase.Phase3_WaitingStart or ConnectionPhase.Phase3_Streaming => 3,
                _ => 0
            };
        }

        private async Task WaitForHardwareAckAsync()
        {
            var buffer = new byte[4096];
            var ms = new System.IO.MemoryStream();

            while (_ws.State == WebSocketState.Open)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(30000); // 30 sec timeout

                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new OperationCanceledException("Client closed connection");

                ms.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                // Debug: Log received message
                var truncated = text.Length > 100 ? text.Substring(0, 100) + "..." : text;
                Logger.Info($"[Protocol] WaitForHardwareAck received: len={text.Length}, text={truncated}");

                // Handle ping (with sequence support)
                if (await TryHandlePingAsync(text))
                {
                    continue;
                }

                // Check for hardware_info_ack
                var msgType = ProtocolMessageParser.GetMessageType(text);
                Logger.Info($"[Protocol] WaitForHardwareAck msgType={msgType}");
                if (msgType == "hardware_info_ack")
                {
                    // Parse client codec capabilities
                    var ackMsg = ProtocolMessageParser.Parse<HardwareInfoAckMessage>(text);
                    if (ackMsg?.ClientCodecs != null)
                    {
                        _clientCodecCapability = ackMsg.ClientCodecs;
                        _clientScreenHeight = ackMsg.ClientCodecs.ScreenHeight;
                        Logger.Info($"[Protocol] Client codec capabilities: HEVC={_clientCodecCapability.SupportsHevc}, " +
                                          $"preferred={_clientCodecCapability.PreferredCodec}, device={_clientCodecCapability.DeviceModel}");
                        Logger.Info($"[Protocol] Client screen resolution: {ackMsg.ClientCodecs.ScreenWidth}x{ackMsg.ClientCodecs.ScreenHeight}");

                        // Negotiate codec: Use H.265 if both server and client support it
                        NegotiateCodec();
                    }
                    else
                    {
                        Logger.Info("[Protocol] No client codec capabilities in hardware_info_ack, using H.264");
                        _selectedCodec = "H264";
                    }

                    // Parse per-track PC mode flag
                    if (ackMsg != null)
                    {
                        _perTrackPc = ackMsg.PerTrackPc;
                        Logger.Info($"[Protocol] Client perTrackPc: {_perTrackPc}");

                        // Multi-monitor streaming intent. Missing JSON field defaults to false
                        // (RemotePlay single-active-monitor policy and legacy fallback).
                        _streamAllMonitors = ackMsg.StreamAllMonitors;
                        Logger.Info(
                            $"[Protocol] Client streamAllMonitors: {_streamAllMonitors} " +
                            $"(stream policy={(ShouldAutoPauseInactiveMonitors(_streamAllMonitors) ? "active-monitor-only" : "all-monitors")})");
                    }

                    return;
                }
            }
        }

        /// <summary>
        /// Negotiate codec based on server and client capabilities.
        /// Respects DisplayConfig.PreferredCodec:
        ///   "H264" / "H265" = force that codec if both sides support it
        ///   "Auto" = default priority H265 > H264 > VP9 > VP8
        /// </summary>
        private void NegotiateCodec()
        {
            // Build server supported codecs list
            var serverCodecs = new List<string>();

            // H264 is always available (hardware or fallback)
            serverCodecs.Add("H264");

            // Check H265 hardware support
            if (_encoderInfo?.SupportsHevc == true)
                serverCodecs.Add("H265");

            // Check VP9/VP8 support (libvpx via FFmpeg)
            if (CheckVpxEncoderAvailable("libvpx-vp9"))
                serverCodecs.Add("VP9");
            if (CheckVpxEncoderAvailable("libvpx"))
                serverCodecs.Add("VP8");

            Logger.Info($"[Protocol] Server supported codecs: [{string.Join(", ", serverCodecs)}]");

            // Get client supported codecs
            var clientCodecs = _clientCodecCapability?.SupportedCodecs ?? new[] { "H264" };
            Logger.Info($"[Protocol] Client supported codecs: [{string.Join(", ", clientCodecs)}]");

            // Check preferred codec from configuration
            var preferred = Configuration.DisplayConfig.PreferredCodec?.ToUpperInvariant() ?? "AUTO";
            Logger.Info($"[Protocol] Preferred codec (config): {preferred}");

            // If a specific codec is preferred and both sides support it, use it directly
            if (preferred != "AUTO")
            {
                if (serverCodecs.Contains(preferred, StringComparer.OrdinalIgnoreCase) &&
                    clientCodecs.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                {
                    _selectedCodec = preferred;
                    Logger.Info($"[Protocol] Codec negotiation: using preferred {_selectedCodec}");
                    return;
                }
                Logger.Warn($"[Protocol] Preferred codec {preferred} not supported by both sides, falling back to auto");
            }

            // Auto mode: find best mutual codec (priority: H265 > H264 > VP9 > VP8)
            string[] priority = { "H265", "H264", "VP9", "VP8" };

            foreach (var codec in priority)
            {
                if (serverCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase) &&
                    clientCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
                {
                    _selectedCodec = codec.ToUpperInvariant();
                    Logger.Info($"[Protocol] Codec negotiation: selected {_selectedCodec}");
                    return;
                }
            }

            // Default fallback to H264
            _selectedCodec = "H264";
            Logger.Info("[Protocol] Codec negotiation: no match found, defaulting to H264");
        }

        /// <summary>
        /// Parse codec string to VideoCodec enum
        /// </summary>
        private static VideoCodec ParseVideoCodec(string codec)
        {
            return codec?.ToUpperInvariant() switch
            {
                "H264" => VideoCodec.H264,
                "H265" or "HEVC" => VideoCodec.H265,
                "VP9" => VideoCodec.VP9,
                "VP8" => VideoCodec.VP8,
                _ => VideoCodec.H264 // Default fallback
            };
        }

        /// <summary>
        /// Check if a VPx encoder is available in FFmpeg.
        /// </summary>
        private bool CheckVpxEncoderAvailable(string encoderName)
        {
            try
            {
                unsafe
                {
                    var codec = FFmpeg.AutoGen.ffmpeg.avcodec_find_encoder_by_name(encoderName);
                    bool available = codec != null;
                    Logger.Info($"[Protocol] Encoder {encoderName}: {(available ? "available" : "not found")}");
                    return available;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] Error checking encoder {encoderName}: {ex.Message}");
                return false;
            }
        }

        private async Task WaitForProceedAsync(int expectedPhase)
        {
            var buffer = new byte[4096];
            var ms = new System.IO.MemoryStream();

            while (_ws.State == WebSocketState.Open)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(300000); // 5 min timeout for user review

                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new OperationCanceledException("Client closed connection");

                ms.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                Logger.Info($"[Protocol] WaitForProceed received: {text.Substring(0, Math.Min(100, text.Length))}...");

                // Handle ping (with sequence support)
                if (await TryHandlePingAsync(text))
                {
                    continue;
                }

                // Check for proceed
                var msgType = ProtocolMessageParser.GetMessageType(text);
                if (msgType == "proceed")
                {
                    var proceed = ProtocolMessageParser.Parse<ProceedMessage>(text);
                    Logger.Info($"[Protocol] Received proceed message, phase={proceed?.Phase}, expected={expectedPhase}");
                    if (proceed?.Phase == expectedPhase)
                        return;
                }
                else if (msgType == "display_config")
                {
                    // Buffer display_config that arrives early (client sends proceed + display_config back-to-back)
                    Logger.Info("[Protocol] Buffering early display_config");
                    _bufferedDisplayConfig = ProtocolMessageParser.Parse<DisplayConfigMessage>(text);
                }
                else if (text.Trim().Equals("proceed", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        private async Task<DisplayConfigMessage> WaitForDisplayConfigAsync()
        {
            // Check if display_config was already buffered (client sends proceed + display_config back-to-back)
            if (_bufferedDisplayConfig != null)
            {
                Logger.Info("[Protocol] Using buffered display_config");
                var buffered = _bufferedDisplayConfig;
                _bufferedDisplayConfig = null;
                return buffered;
            }

            var buffer = new byte[4096];
            var ms = new System.IO.MemoryStream();

            while (_ws.State == WebSocketState.Open)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(60000); // 1 min timeout

                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new OperationCanceledException("Client closed connection");

                ms.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                Logger.Info($"[Protocol] WaitForDisplayConfig received: {text.Substring(0, Math.Min(100, text.Length))}...");

                // Handle ping (with sequence support)
                if (await TryHandlePingAsync(text))
                {
                    continue;
                }

                var msgType = ProtocolMessageParser.GetMessageType(text);
                if (msgType == "display_config")
                {
                    var config = ProtocolMessageParser.Parse<DisplayConfigMessage>(text);
                    if (config != null)
                        return config;
                }
            }

            throw new OperationCanceledException("Did not receive display_config");
        }

        private async Task WaitForStartStreamingAsync()
        {
            // If start_streaming was already received during Phase 2 ICE exchange
            // (happens after restart_phase2 when client sends it early), skip waiting
            if (_startStreamingReceived)
            {
                _startStreamingReceived = false;
                Logger.Info("[Protocol] start_streaming already received during Phase 2, skipping wait");
                return;
            }

            var buffer = new byte[4096];
            var ms = new System.IO.MemoryStream();

            while (_ws.State == WebSocketState.Open)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(60000); // 1 min timeout

                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new OperationCanceledException("Client closed connection");

                ms.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                // Handle ping (with sequence support)
                if (await TryHandlePingAsync(text))
                {
                    continue;
                }

                var msgType = ProtocolMessageParser.GetMessageType(text);
                if (msgType == "start_streaming" || text.Equals("start_streaming", StringComparison.OrdinalIgnoreCase))
                    return;

                // Per-track PC mode: video signaling messages (video_answer, video_candidate)
                // arrive DURING this wait because client sends them after video_offer but
                // before start_streaming. Must forward them to handlers or they're silently lost.
                if (_perTrackPc)
                {
                    if (msgType == "video_answer")
                    {
                        await HandleVideoAnswerAsync(text);
                        continue;
                    }
                    if (msgType == "video_candidate")
                    {
                        HandleVideoIceCandidate(text);
                        continue;
                    }
                }
            }

            throw new OperationCanceledException("Did not receive start_streaming");
        }

        private async Task SendMessageAsync<T>(T message) where T : ProtocolMessage
        {
            var json = ProtocolMessageParser.Serialize(message);
            await SendTextAsync(json);
        }

        private async Task SendTextAsync(string text)
        {
            if (_ws.State != WebSocketState.Open) return;

            if (!await _sendLock.WaitAsync(5000, _ct))
            {
                Logger.Error($"[Protocol] SendTextAsync timed out waiting for lock (len={text.Length})");
                return;
            }

            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(5000); // 5 sec timeout
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Warn($"[Protocol] SendTextAsync timed out or cancelled (len={text.Length})");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] SendTextAsync error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Send a binary WS frame to the client (relay-media path). Shares the same
        /// send lock as SendTextAsync so media chunks and signaling never interleave
        /// on the underlying socket.
        /// lockTimeoutMs >= 0: drop the payload if the lock isn't acquired in time —
        /// ONLY for loss-tolerant per-packet channels (audio PCM: one lost packet is a
        /// 10ms glitch). lockTimeoutMs &lt; 0: wait indefinitely — used by the relay
        /// video send loop, where load shedding happens upstream at FRAME granularity
        /// (dropping a mid-frame chunk corrupts the NAL and poisons all later P-frames).
        /// </summary>
        private async Task SendBytesAsync(byte[] data, int lockTimeoutMs = 1000)
        {
            if (_ws.State != WebSocketState.Open) return;
            if (lockTimeoutMs < 0)
                await _sendLock.WaitAsync(_ct);
            else if (!await _sendLock.WaitAsync(lockTimeoutMs, _ct))
                return; // drop under backpressure (loss-tolerant channels only)

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(5000);
                await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] SendBytesAsync error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Handles ping messages with sequence support for accurate RTT measurement.
        /// Supports both "ping" and "ping:N" formats.
        /// Returns true if the message was a ping, false otherwise.
        /// </summary>
        private async Task<bool> TryHandlePingAsync(string text)
        {
            var trimmed = text.Trim();

            // Handle sequenced ping: "ping:N" -> "pong:N"
            if (trimmed.StartsWith("ping:", StringComparison.OrdinalIgnoreCase))
            {
                // Track client activity for keepalive
                _lastPongReceived = DateTime.UtcNow;
                _missedPongs = 0;

                // Extract sequence number and echo it back
                var seq = trimmed.Substring(5);
                await SendTextAsync($"pong:{seq}");
                return true;
            }

            // Handle legacy ping: "ping" -> "pong"
            if (trimmed.Equals("ping", StringComparison.OrdinalIgnoreCase))
            {
                // Track client activity for keepalive
                _lastPongReceived = DateTime.UtcNow;
                _missedPongs = 0;

                await SendTextAsync("pong");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Handles a client <c>restart_phase2</c> request (full Phase-2 ICE renegotiation) with the
        /// restart-count cap. Shared by the Phase-2 AND Phase-3 message loops (H1): the client sends
        /// <c>restart_phase2</c> mid-stream when an ICE restart exhausts its budget, or immediately
        /// for every trigger when the host did not advertise <c>supportsIceRestart</c>. Previously
        /// only the Phase-2 loop handled it, so a mid-stream fallback was silently dropped.
        /// Returns true if the cap was exceeded (WS already closed → caller must exit its loop).
        /// </summary>
        private async Task<bool> HandleRestartPhase2RequestAsync()
        {
            // Already on the media relay — ignore full-renegotiation requests so we don't
            // tear down the working relay session. Background ICE restart still runs and
            // will auto-upgrade to P2P if it ever connects.
            if (_mediaRelayMode) return false;

            _phase2RestartCount++;
            if (_phase2RestartCount > MAX_PHASE2_RESTARTS)
            {
                Logger.Error($"[Protocol] Phase 2 restart limit reached ({_phase2RestartCount}/{MAX_PHASE2_RESTARTS}), closing for full reconnect");
                try { await SendTextAsync("{\"type\":\"connection_failed\",\"reason\":\"max_restarts_exceeded\",\"message\":\"Connection failed. Reconnecting...\"}"); } catch { }
                // Force close WebSocket — client will auto-reconnect with fresh state
                try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "restart_limit_exceeded", CancellationToken.None); } catch { }
                return true;
            }
            Logger.Info($"[Protocol] Client requested Phase 2 restart ({_phase2RestartCount}/{MAX_PHASE2_RESTARTS})");
            await HandleRestartPhase2Async();
            return false;
        }

        /// <summary>
        /// Handle Phase 2 restart request from client.
        /// Lightweight restart: stops PeerConnection but keeps capture and streamer alive
        /// for fast ICE renegotiation (~300ms vs 2-5s with full teardown).
        /// </summary>
        private async Task HandleRestartPhase2Async()
        {
            Logger.Info("[Protocol] Handling restart_phase2 request...");

            try
            {
                // 0. Stop adaptive-FPS coordinator FIRST (blocking dispose waits for any in-flight
                // tick) so it can never call UpdateConfig/SetFps into the streamer we stop below.
                // StartCaptureThread() creates a fresh one after DTLS re-negotiation.
                try { _adaptiveFpsCoordinator?.Dispose(); } catch { }
                _adaptiveFpsCoordinator = null;

                // 1. Stop current PeerConnection (lightweight - keep streamer alive)
                if (_streamer != null)
                {
                    Logger.Info("[Protocol] Stopping current stream for restart...");
                    _streamer.Stop(); // Closes PC but preserves event handlers + device mappings
                }

                // 2. Stop capture threads (keep D3D11 devices + DXGI duplication alive)
                // Cancel the Protocol-Capture thread first so StartCaptureThread() can re-create it.
                // Without this, the thread stays alive (blocked on WaitHandle) and prevents
                // _capture.Start() from being called on the next connection attempt → zero frames.
                try { _captureCts?.Cancel(); } catch { }

                try { _captureThread?.Join(2000); } catch { }
                _captureThread = null;
                if (_sharedCapture != null)
                {
                    Logger.Info("[Protocol] Stopping capture for restart...");
                    _sharedCapture.Stop(); // Stops threads, restarts via StartCaptureThread() after DTLS
                }

                // 3. Reset DTLS completion gate for new negotiation
                _allConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // 4. Get current monitor count from display config
                int actualMonitors = _displayConfig?.Monitors ?? _monitors.Count;
                actualMonitors = Math.Min(actualMonitors, _monitors.Count);

                // 4. Send config_complete to client to trigger new ICE negotiation
                // (fresh ephemeral TURN credentials so the new PCs can allocate)
                Logger.Info("[Protocol] Sending config_complete for Phase 2 restart");
                var sessionIce = TurnCredentialProvider.GetSessionIceServers();
                var completeMsg = new ConfigCompleteMessage
                {
                    Monitors = _monitors.Take(actualMonitors).Select((m, i) => new MonitorInfoDto
                    {
                        Id = i,
                        Name = m.name,
                        Width = m.width,
                        Height = m.height
                    }).ToList(),
                    CaptureReady = false, // Will be ready after new ICE negotiation
                    IceServers = sessionIce.Count > 0 ? sessionIce : null
                };
                await SendMessageAsync(completeMsg);

                Logger.Info("[Protocol] Phase 2 restart config_complete sent, waiting for ICE negotiation...");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] Error handling restart_phase2: {ex.Message}");
                await SendMessageAsync(new ErrorMessage
                {
                    Phase = 2,
                    Code = "RESTART_FAILED",
                    Message = $"Failed to restart Phase 2: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handle reconnect acknowledgment from client.
        /// Called when client successfully reconnected a monitor.
        /// </summary>
        private void HandleReconnectAck(string json)
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("monitorIndex", out var monIdxElem))
                {
                    int monitorIndex = monIdxElem.GetInt32();
                    Logger.Info($"[Protocol] Client acknowledged reconnect for monitor {monitorIndex}");

                    // Could track reconnect state here if needed
                    // _pendingReconnects.TryRemove(monitorIndex, out _);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] Error parsing reconnect_ack: {ex.Message}");
            }
        }

        private async Task SendProgressAsync(string step, int progress, string message)
        {
            var msg = new ConfigProgressMessage
            {
                Step = step,
                Progress = progress,
                Message = message
            };
            await SendMessageAsync(msg);
        }

        private async Task SendErrorAsync(int phase, string code, string message)
        {
            try
            {
                var msg = new ErrorMessage
                {
                    Phase = phase,
                    Code = code,
                    Message = message
                };
                await SendMessageAsync(msg);
            }
            catch { }
        }

        private async Task CleanupAsync()
        {
            SetPhase(ConnectionPhase.Disconnecting);

            // Phase 1 pairing: release this session's paired-fingerprint binding (does NOT
            // unpair the peer — PairingStore keeps the fingerprint for the next reconnect).
            // Prevents a stale binding from lingering in PeerAuthGate's in-memory dictionary
            // after the session ends. No-op when RequirePairing is OFF (never touches the
            // singleton at all, matching every other gate's legacy-unchanged guarantee).
            if (PairingPolicy.RequirePairing)
                PeerAuthGate.Instance.Unbind(_clientId);

            // Stop keep-alive timer
            StopKeepAlive();

            // Stop capture — order matters to avoid ObjectDisposedException:
            // 1. Signal capture thread to stop
            // 2. Stop capture loops (waits for capture threads to exit)
            // 3. Join our protocol capture thread
            // 4. Dispose TextureResizer (now safe — no capture thread is using it)
            // 5. Dispose shared capture
            try { _captureCts?.Cancel(); } catch { }

            // Stop adaptive-FPS coordinator BEFORE disposing the streamer it drives.
            try { _adaptiveFpsCoordinator?.Dispose(); } catch { }
            _adaptiveFpsCoordinator = null;

            // Stop capture loops BEFORE disposing TextureResizer
            lock (_captureLock)
            {
                try { _sharedCapture?.Stop(); } catch { }
            }

            try { _captureThread?.Join(2000); } catch { }

            // Dispose streamer
            _streamer?.Dispose();
            _streamer = null;

            // Dispose texture resizer — safe now that all capture threads have stopped
            _textureResizer?.Dispose();
            _textureResizer = null;

            // Cleanup shared capture
            lock (_captureLock)
            {
                if (_sharedCapture != null)
                {
                    _sharedCapture.Dispose();
                    _sharedCapture = null;
                }
            }

            // Stop safety monitor before display restore
            _safetyMonitor?.Stop();
            _safetyMonitor = null;

            // Clear VDD-only flag before restore (prevents timer from re-triggering)
            if (DisplayConfig.MonitorType == "bind_mobile")
                DisplayGuard.ClearVddOnlyActive();

            // Restore display settings if they were modified
            // This runs even if _sharedCapture was already disposed (e.g., during reconnect attempts)
            if (_displayModified)
            {
                Logger.Info("[Protocol] Restoring display settings...");
                try
                {
                    DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(15));
                    Logger.Info("[Protocol] Display settings restored.");
                    _displayModified = false;

                    // Reset monitor count and type to force VDD setup on next session
                    DisplayConfig.MonitorCount = 1;
                    DisplayConfig.MonitorType = "standard";
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Protocol] Restore failed: {ex.Message}");
                }
            }

            // Close WebSocket (with timeout to avoid hanging on dead tunnel connections)
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(3000);
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
                }
            }
            catch
            {
                // Graceful close failed (dead connection) — force abort
                try { _ws.Abort(); } catch { }
            }

            Logger.Info($"[Protocol] Client {_clientId} disconnected");
        }
    }
}
