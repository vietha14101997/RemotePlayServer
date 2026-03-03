#nullable enable
using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol
{
    public partial class PhaseProtocolHandler
    {
        private async Task RunPhase3Async()
        {
            SetPhase(ConnectionPhase.Phase3_WaitingStart);
            Logger.Info("[Protocol] Phase 3: Waiting for start_streaming command...");

            // Wait for start_streaming
            await WaitForStartStreamingAsync();

            // CRITICAL: Wait for ALL monitors to be ICE connected before starting streaming
            // This prevents network congestion from one monitor's stream interfering with
            // another monitor's ICE negotiation
            if (_allConnectedTcs != null)
            {
                Logger.Info("[Protocol] Phase 3: Waiting for all monitors to connect...");
                try
                {
                    // Wait up to 15 seconds for all monitors to connect
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var allConnectedTask = _allConnectedTcs.Task;
                    var completedTask = await Task.WhenAny(allConnectedTask, Task.Delay(Timeout.Infinite, cts.Token));

                    if (completedTask == allConnectedTask)
                    {
                        Logger.Info("[Protocol] Phase 3: All monitors connected!");
                    }
                    else
                    {
                        Logger.Info("[Protocol] Phase 3: Timeout waiting for all monitors, proceeding anyway");
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("[Protocol] Phase 3: Timeout waiting for all monitors, proceeding anyway");
                }
            }

            SetPhase(ConnectionPhase.Phase3_Streaming);
            Logger.Info("[Protocol] Phase 3: Starting stream...");

            // Reset sync clocks to clear stale early-capture RTP state.
            // Early capture sends frames before client is ready (99%+ loss),
            // which inflates the client's jitter buffer. Fresh time origin
            // ensures Phase 3 frames start with clean RTP timestamps.
            _streamer?.ResetSyncState();

            // Activate Phase 3 via barrier sync — ensures all tracks see
            // _phase3Active=true at the same barrier cycle, preventing one track
            // from starting 1-2 cycles before the other (27ms RTP offset).
            // NOTE: Single-monitor mode (e.g. ultrawide) has no barrier,
            // so we must activate immediately in that case.
            if (_capture != null && _streamer != null && _capture.HasBarrierSync)
            {
                _capture.OnNextBarrierSync = () => _streamer?.ActivatePhase3();
            }
            else
            {
                // No barrier (single monitor) or no capture — activate immediately
                _streamer?.ActivatePhase3();
            }

            // Send streaming_started
            var startedMsg = new StreamingStartedMessage
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            await SendMessageAsync(startedMsg);

            // Start capture thread
            StartCaptureThread();

            // Start timing sync task
            var timingSyncTask = StartTimingSyncTask();

            // Start cursor tracking
            var cursorTrackingTask = StartCursorTrackingTask();
            Logger.Info("[Protocol] Cursor tracking started");

            // Start server-side keep-alive for early disconnect detection
            StartKeepAlive();

            // Start server-side stall detection for proactive recovery
            StartStallDetection();

            // Main loop - handle messages while streaming
            var buffer = new byte[128 * 1024];
            var ms = new System.IO.MemoryStream();

            while (_ws.State == WebSocketState.Open && !_ct.IsCancellationRequested)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                    cts.CancelAfter(30000);

                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    ms.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);

                    // Handle ping/pong (with sequence support) and stop_streaming
                    if (await TryHandlePingAsync(text))
                    {
                        continue;
                    }

                    // Track client pong responses for keep-alive
                    if (text.Trim().Equals("pong", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastPongReceived = DateTime.UtcNow;
                        _missedPongs = 0;
                        continue;
                    }

                    var msgType = ProtocolMessageParser.GetMessageType(text);

                    // Log important Phase 3 messages (skip high-frequency ones)
                    if (msgType != "quality_feedback" && msgType != "fps_feedback"
                        && msgType != "request_keyframe" && msgType != "frameTiming"
                        && !text.StartsWith("ping:"))
                    {
                        Logger.Info($"[Protocol] Phase3 RX: type={msgType ?? "null"}, len={text.Length}");
                    }

                    if (msgType == "stop_streaming" || text.Equals("stop_streaming", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("[Protocol] Received stop_streaming");
                        break;
                    }

                    // Handle pause_streaming - stop capture/encode but keep connection
                    if (msgType == "pause_streaming")
                    {
                        Logger.Info("[Protocol] Received pause_streaming");
                        _streamer?.Pause();
                        continue;
                    }

                    // Handle resume_streaming - restart capture/encode
                    if (msgType == "resume_streaming")
                    {
                        Logger.Info("[Protocol] Received resume_streaming");
                        _streamer?.Resume();
                        continue;
                    }

                    // Handle pause_monitor - pause a specific monitor (stops both capture and encode)
                    if (msgType == "pause_monitor")
                    {
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(text);
                            if (json.RootElement.TryGetProperty("monitorIndex", out var mi))
                            {
                                int monitorIndex = mi.GetInt32();
                                Logger.Info($"[Protocol] Received pause_monitor: index={monitorIndex}");
                                // Pause capture (stops DXGI frame acquisition)
                                _capture?.PauseMonitor(monitorIndex);
                                // Pause encode (blocks any leftover frames from being encoded)
                                _streamer?.PauseMonitor(monitorIndex);
                            }
                        }
                        catch { }
                        continue;
                    }

                    // Handle resume_monitor - resume a specific monitor
                    if (msgType == "resume_monitor")
                    {
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(text);
                            if (json.RootElement.TryGetProperty("monitorIndex", out var mi))
                            {
                                int monitorIndex = mi.GetInt32();
                                Logger.Info($"[Protocol] Received resume_monitor: index={monitorIndex}");
                                // Resume capture (resumes DXGI frame acquisition)
                                _capture?.ResumeMonitor(monitorIndex);
                                // Resume encode (allows encoding and sends keyframe)
                                _streamer?.ResumeMonitor(monitorIndex);
                            }
                        }
                        catch { }
                        continue;
                    }

                    // Handle late ICE candidates
                    if (msgType == "candidate")
                    {
                        await HandleJsonMessageAsync(text, msgType);
                        continue;
                    }
                    if (text.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleLegacyMessageAsync(text);
                        continue;
                    }

                    // Handle reconnect offers from client (client-side auto-heal)
                    if (msgType == "offer")
                    {
                        Logger.Info("[Protocol] Received reconnect offer during streaming (JSON format)");
                        // Reset stall detection state for fresh reconnection
                        _consecutiveStallCount = 0;
                        _feedbackEstablished = false;
                        Interlocked.Exchange(ref _lastClientFeedbackTicks, DateTime.UtcNow.Ticks);
                        await HandleJsonMessageAsync(text, msgType);
                        continue;
                    }
                    if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("[Protocol] Received reconnect offer during streaming (legacy format)");
                        // Reset stall detection state for fresh reconnection
                        _consecutiveStallCount = 0;
                        _feedbackEstablished = false;
                        Interlocked.Exchange(ref _lastClientFeedbackTicks, DateTime.UtcNow.Ticks);
                        await HandleLegacyMessageAsync(text);
                        continue;
                    }

                    // Handle end_of_candidates from client
                    if (msgType == "end_of_candidates")
                    {
                        Logger.Info("[Protocol] Received end_of_candidates during streaming");
                        continue;
                    }

                    // Handle keyframe request from client (for immediate visual update on interaction)
                    if (msgType == "request_keyframe")
                    {
                        int monitorIndex = -1; // -1 means all monitors
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(text);
                            if (json.RootElement.TryGetProperty("monitorIndex", out var mi))
                                monitorIndex = mi.GetInt32();
                        }
                        catch { }

                        _streamer?.RequestKeyframe(monitorIndex);
                        continue;
                    }

                    // Handle keyframe burst request (send N consecutive I-frames for WiFi resilience)
                    if (msgType == "request_keyframe_burst")
                    {
                        int monitorIndex = -1;
                        int count = 3;
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(text);
                            if (json.RootElement.TryGetProperty("monitorIndex", out var mi))
                                monitorIndex = mi.GetInt32();
                            if (json.RootElement.TryGetProperty("count", out var c))
                                count = c.GetInt32();
                        }
                        catch { }

                        _streamer?.RequestKeyframeBurst(monitorIndex, Math.Clamp(count, 1, 5));
                        continue;
                    }

                    // Handle skip_to_live request from client (for latency recovery)
                    // Client sends this when it detects accumulated delay > threshold
                    // Supports optional "monitor" field for per-monitor sync
                    if (msgType == "skip_to_live")
                    {
                        int monitorIndex = -1; // -1 means all monitors
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(text);
                            if (json.RootElement.TryGetProperty("monitor", out var mi))
                                monitorIndex = mi.GetInt32();
                        }
                        catch { }

                        Logger.Debug($"[Protocol] skip_to_live received (monitor={monitorIndex}) - forcing keyframe for latency recovery");

                        // Force keyframe on specified monitor (or all if -1)
                        _streamer?.RequestKeyframe(monitorIndex);

                        // Send acknowledgment with server timestamp
                        try
                        {
                            var ackJson = $"{{\"type\":\"skip_to_live_ack\",\"monitor\":{monitorIndex},\"serverTime\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                            await _ws.SendAsync(
                                new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(ackJson)),
                                System.Net.WebSockets.WebSocketMessageType.Text,
                                true,
                                _ct);
                        }
                        catch { }
                        continue;
                    }

                    // Handle fps_feedback from client for adaptive encoding
                    if (msgType == "fps_feedback")
                    {
                        try
                        {
                            var feedback = ProtocolMessageParser.Parse<FpsFeedbackMessage>(text);
                            if (feedback != null && _streamer != null)
                            {
                                Interlocked.Exchange(ref _lastClientFeedbackTicks, DateTime.UtcNow.Ticks);
                                _feedbackEstablished = true;
                                _consecutiveStallCount = 0; // Reset escalation on real feedback
                                _streamer.ProcessFpsFeedback(
                                    feedback.MonitorIndex,
                                    feedback.EffectiveFps,
                                    feedback.DroppedFrames,
                                    feedback.TotalFrames);

                                // Send acknowledgment with current target FPS
                                var ack = new FpsAdjustedMessage
                                {
                                    MonitorIndex = feedback.MonitorIndex,
                                    TargetFps = _streamer.GetCurrentTargetFps(feedback.MonitorIndex)
                                };
                                await SendMessageAsync(ack);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Protocol] fps_feedback error: {ex.Message}");
                        }
                        continue;
                    }

                    // Handle quality_feedback from client for adaptive bitrate
                    if (msgType == "quality_feedback")
                    {
                        try
                        {
                            var feedback = ProtocolMessageParser.Parse<QualityFeedbackMessage>(text);
                            if (feedback != null && _streamer != null)
                            {
                                Interlocked.Exchange(ref _lastClientFeedbackTicks, DateTime.UtcNow.Ticks);
                                _feedbackEstablished = true;
                                _consecutiveStallCount = 0; // Reset escalation on real feedback
                                // Proactive keyframe burst on significant packet loss
                                if (feedback.PacketLossRate > 0.02f)
                                    _streamer.RequestKeyframeBurst(-1, 3);

                                // WiFi-aware adaptive bitrate
                                _streamer.SetWiFiMode(feedback.IsWiFi);

                                var bitrateResult = _streamer.ProcessQualityFeedback(feedback);
                                if (bitrateResult != null)
                                {
                                    await SendMessageAsync(bitrateResult);
                                    Logger.Info($"[Protocol] Bitrate adjusted: {bitrateResult.BitrateKbps}kbps - {bitrateResult.Reason}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Protocol] quality_feedback error: {ex.Message}");
                        }
                        continue;
                    }

                    // Handle update_config from client for dynamic FPS/Bitrate changes
                    if (msgType == "update_config")
                    {
                        try
                        {
                            var updateMsg = ProtocolMessageParser.Parse<UpdateConfigMessage>(text);
                            if (updateMsg != null && _streamer != null)
                            {
                                Logger.Info($"[Protocol] update_config received: fps={updateMsg.Fps}, bitrate={updateMsg.BitrateKbps}kbps");

                                var (success, appliedFps, appliedBitrate, message) = _streamer.UpdateConfig(
                                    updateMsg.Fps,
                                    updateMsg.BitrateKbps);

                                // Also update capture FPS if FPS was changed
                                if (updateMsg.Fps.HasValue && _capture != null)
                                {
                                    _capture.SetTargetFps(updateMsg.Fps.Value);
                                }

                                // Send acknowledgment
                                var ack = new ConfigUpdatedMessage
                                {
                                    Fps = appliedFps,
                                    BitrateKbps = appliedBitrate,
                                    Success = success,
                                    Message = message
                                };
                                await SendMessageAsync(ack);
                                Logger.Info($"[Protocol] config_updated sent: {message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Protocol] update_config error: {ex.Message}");
                        }
                        continue;
                    }
                }
                catch (OperationCanceledException) { break; }
            }

            // Stop keep-alive timer
            StopKeepAlive();

            // Stop stall detection timer
            StopStallDetection();

            // Stop cursor tracking
            StopCursorTracking();
            Logger.Info("[Protocol] Cursor tracking stopped");

            Logger.Info("[Protocol] Phase 3: Stream ended");
        }

        private void StartCaptureThread()
        {
            if (_capture == null || _streamer == null) return;

            // Prevent duplicate start
            if (_captureThread != null && _captureThread.IsAlive)
            {
                Logger.Info("[Protocol] Capture thread already running, skipping start");
                return;
            }

            _captureCts = new CancellationTokenSource();
            _captureThread = new Thread(() =>
            {
                try { RoInitialize(1); } catch { }
                try
                {
                    // NV12 frame handler (standard path with color conversion)
                    _capture.OnMonitorFrame += (monitorIndex, nv12Texture, w, h, timestamp) =>
                    {
                        _streamer?.PushTexture(monitorIndex, nv12Texture, w, h, timestamp);

                        if (monitorIndex == 0)
                        {
                            var fn = Interlocked.Increment(ref _frameCount);
                            _frameTiming.Enqueue((fn, timestamp));
                            while (_frameTiming.Count > 30) _frameTiming.TryDequeue(out _);
                        }
                    };

                    // BGRA frame handler (zero-copy path, no color conversion)
                    _capture.OnMonitorFrameBgra += (monitorIndex, bgraTexture, w, h, timestamp) =>
                    {
                        // Resize texture if it exceeds max resolution (1440x810)
                        ID3D11Texture2D? textureToSend = bgraTexture;
                        int targetWidth = w;
                        int targetHeight = h;

                        // Capture local ref to avoid TOCTOU race during shutdown
                        var resizer = _textureResizer;
                        if (resizer != null)
                        {
                            try
                            {
                                if (resizer.NeedsResize(w, h))
                                {
                                    var device = _capture?.GetDeviceForMonitor(monitorIndex);
                                    if (device != null)
                                    {
                                        var (resized, rw, rh) = resizer.ResizeBgraTexture(device, bgraTexture, w, h, monitorIndex);
                                        textureToSend = resized;
                                        targetWidth = rw;
                                        targetHeight = rh;
                                    }
                                }
                            }
                            catch (ObjectDisposedException) { return; }
                        }

                        // Push texture (null-forgiving since textureToSend is always non-null)
                        _streamer?.PushBgraTexture(monitorIndex, textureToSend!, targetWidth, targetHeight, timestamp);

                        if (monitorIndex == 0)
                        {
                            var fn = Interlocked.Increment(ref _frameCount);
                            _frameTiming.Enqueue((fn, timestamp));
                            while (_frameTiming.Count > 30) _frameTiming.TryDequeue(out _);
                        }
                    };

                    // Enable deferred multi-track sending if multiple monitors are active.
                    // Post-encode barrier syncs all monitors after encoding, then sends
                    // all tracks' RTP packets in alternating order to prevent jitter asymmetry.
                    int activeMonitors = _capture.Monitors.Count(m => m.Device != null && m.Duplication != null);
                    if (activeMonitors > 1 && _streamer != null)
                    {
                        _streamer.DeferredSendEnabled = true;
                        _capture.OnPostEncodeSync += () => _streamer?.FlushAllPendingFrames();
                        Logger.Info($"[Protocol] Deferred send enabled: {activeMonitors} monitors, alternating RTP send order");
                    }

                    _capture.Start();
                    _captureCts.Token.WaitHandle.WaitOne();
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Protocol] Capture error: {ex.Message}");
                }
            })
            { IsBackground = true, Name = "Protocol-Capture" };
            _captureThread.Start();
        }

        private Task StartTimingSyncTask()
        {
            return Task.Run(async () =>
            {
                long lastTimingSyncTime = 0;
                while (!_captureCts?.Token.IsCancellationRequested == true && _ws.State == WebSocketState.Open)
                {
                    try
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (now - lastTimingSyncTime >= 1000)
                        {
                            lastTimingSyncTime = now;
                            var recentFrames = _frameTiming.ToArray().TakeLast(10).ToArray();
                            if (recentFrames.Length > 0)
                            {
                                var timingData = new
                                {
                                    type = "frameTiming",
                                    serverTime = now,
                                    currentFrame = _frameCount,
                                    recentFrames = recentFrames.Select(f => new { frameNum = f.frameNum, captureTime = f.captureTime }).ToArray()
                                };
                                var json = JsonSerializer.Serialize(timingData);
                                await SendTextAsync(json);
                            }
                        }
                        await Task.Delay(500, _captureCts?.Token ?? _ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            });
        }

        /// <summary>
        /// Start server-side keep-alive timer for early disconnect detection.
        /// Pings client every 5s and detects dead connections within 15s (vs 30s ICE timeout).
        /// </summary>
        private void StartKeepAlive()
        {
            _lastPongReceived = DateTime.UtcNow;
            _missedPongs = 0;

            _keepAliveTimer = new System.Timers.Timer(KEEPALIVE_INTERVAL_MS);
            _keepAliveTimer.Elapsed += async (s, e) =>
            {
                try
                {
                    if (_ws.State != WebSocketState.Open)
                    {
                        _keepAliveTimer?.Stop();
                        return;
                    }

                    // Check if client has responded to previous pings
                    var timeSinceLastPong = (DateTime.UtcNow - _lastPongReceived).TotalMilliseconds;
                    if (timeSinceLastPong > KEEPALIVE_INTERVAL_MS * 1.5)
                    {
                        _missedPongs++;
                        Logger.Info($"[KeepAlive] Missed pong #{_missedPongs}, {timeSinceLastPong / 1000:F1}s since last response");

                        if (_missedPongs >= MAX_MISSED_PONGS)
                        {
                            Logger.Info($"[KeepAlive] Client not responding for {timeSinceLastPong / 1000:F1}s - closing connection");
                            _keepAliveTimer?.Stop();

                            // Actually close the connection instead of just warning
                            try
                            {
                                await _ws.CloseOutputAsync(
                                    WebSocketCloseStatus.EndpointUnavailable,
                                    "Client not responding to keepalive",
                                    CancellationToken.None);
                            }
                            catch (Exception closeEx)
                            {
                                Logger.Error($"[KeepAlive] Error closing WebSocket: {closeEx.Message}");
                            }
                            return;
                        }
                    }

                    // Send ping to client
                    await SendTextAsync("ping");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[KeepAlive] Error: {ex.Message}");
                }
            };
            _keepAliveTimer.AutoReset = true;
            _keepAliveTimer.Start();
            Logger.Info("[KeepAlive] Server-side keep-alive started (5s interval)");
        }

        /// <summary>
        /// Stop keep-alive timer.
        /// </summary>
        private void StopKeepAlive()
        {
            try
            {
                _keepAliveTimer?.Stop();
                _keepAliveTimer?.Dispose();
                _keepAliveTimer = null;
            }
            catch { }
        }

        // Stall detection fields
        private System.Timers.Timer? _stallDetectTimer;
        private int _consecutiveStallCount;
        private const int STALL_CHECK_INTERVAL_MS = 200;    // Check every 200ms for sub-second detection
        private const int STALL_THRESHOLD_MS = 800;          // 800ms without feedback = stall (cloud gaming ≤1s target)
        private const int MAX_CONSECUTIVE_STALLS = 10;       // After 10 stalls (~8s), stop acting — let KeepAlive handle it

        /// <summary>
        /// Start server-side stall detection with escalating response.
        /// Uses ForceSetBitrate to bypass adaptive controller cooldowns for immediate effect.
        /// Escalation: 1st stall = 25% cut, 2nd = 50% cut, 3rd = drop to minimum.
        /// Stall 4+: maintain minimum bitrate, NO keyframe bursts (counterproductive on stalled network).
        /// Stall 10+: stop acting entirely (clearly dead connection, KeepAlive will close it).
        /// </summary>
        private void StartStallDetection()
        {
            Interlocked.Exchange(ref _lastClientFeedbackTicks, DateTime.UtcNow.Ticks);
            _consecutiveStallCount = 0;

            _stallDetectTimer = new System.Timers.Timer(STALL_CHECK_INTERVAL_MS);
            _stallDetectTimer.Elapsed += (s, e) =>
            {
                try
                {
                    // Capture locally to prevent null race between check and use
                    var streamer = _streamer;
                    if (_ws.State != WebSocketState.Open || streamer == null)
                    {
                        _stallDetectTimer?.Stop();
                        return;
                    }

                    // Don't detect stalls until client has sent at least one feedback
                    // (avoids false positives during initial connection setup)
                    if (!_feedbackEstablished) return;

                    var lastFeedback = new DateTime(Interlocked.Read(ref _lastClientFeedbackTicks));
                    var timeSinceLastFeedback = (DateTime.UtcNow - lastFeedback).TotalMilliseconds;
                    if (timeSinceLastFeedback > STALL_THRESHOLD_MS)
                    {
                        _consecutiveStallCount++;

                        // After MAX_CONSECUTIVE_STALLS, stop acting — the connection is dead.
                        // KeepAlive will close it. Continuing to ForceSetBitrate/RequestKeyframeBurst
                        // on a dead connection risks native encoder crashes and wastes CPU.
                        if (_consecutiveStallCount > MAX_CONSECUTIVE_STALLS)
                        {
                            if (_consecutiveStallCount == MAX_CONSECUTIVE_STALLS + 1)
                                Logger.Info($"[StallDetect] {MAX_CONSECUTIVE_STALLS} consecutive stalls — stopped acting, waiting for KeepAlive");
                            return;
                        }

                        var (currentFps, currentBitrate, _) = streamer.GetCurrentConfig();

                        // Escalating response based on consecutive stall count
                        int newBitrate;
                        string severity;
                        bool sendKeyframeBurst;
                        switch (_consecutiveStallCount)
                        {
                            case 1:
                                // First stall: 25% cut - could be momentary blip
                                newBitrate = Math.Max(2000, (int)(currentBitrate * 0.75));
                                severity = "moderate (25% cut)";
                                sendKeyframeBurst = true;
                                break;
                            case 2:
                                // Second stall: 50% cut - clear sustained problem
                                newBitrate = Math.Max(2000, currentBitrate / 2);
                                severity = "aggressive (50% cut)";
                                sendKeyframeBurst = true;
                                break;
                            case 3:
                                // Third stall: drop to minimum immediately
                                newBitrate = 2000;
                                severity = "emergency (minimum)";
                                sendKeyframeBurst = true;
                                break;
                            default:
                                // 4th+ stall: already at minimum, NO keyframe burst.
                                // Keyframes are larger than P-frames and pile up in the
                                // send buffer on a stalled network, making congestion worse
                                // and risking OOM in the RTP layer.
                                newBitrate = 2000;
                                severity = "sustain (minimum, no burst)";
                                sendKeyframeBurst = false;
                                break;
                        }

                        // Safety: stall detection must NEVER increase bitrate.
                        // This can happen if stall count resets (feedback arrived between stalls)
                        // and the new stall #1 calculates a higher value than the current actual bitrate.
                        if (newBitrate >= currentBitrate && currentBitrate > 2000)
                        {
                            newBitrate = Math.Max(2000, (int)(currentBitrate * 0.75));
                            severity = $"clamped (was {severity}, would increase)";
                        }

                        Logger.Info($"[StallDetect] No feedback for {timeSinceLastFeedback / 1000:F1}s, stall #{_consecutiveStallCount} → {severity}: {currentBitrate} → {newBitrate}kbps");

                        // Bypass adaptive controller - directly set encoder bitrate
                        streamer.ForceSetBitrate(newBitrate);

                        // Only send keyframe burst for first 3 stalls (escalation phase)
                        if (sendKeyframeBurst)
                            streamer.RequestKeyframeBurst(-1, 3);

                        // Reset timer so we don't spam reductions every 200ms
                        Interlocked.Exchange(ref _lastClientFeedbackTicks, DateTime.UtcNow.Ticks);

                        Logger.Info($"[StallDetect] Applied: {severity}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[StallDetect] Error: {ex.Message}");
                }
            };
            _stallDetectTimer.AutoReset = true;
            _stallDetectTimer.Start();
            Logger.Info("[StallDetect] Server-side stall detection started (800ms threshold, escalating response)");
        }

        /// <summary>
        /// Stop stall detection timer.
        /// </summary>
        private void StopStallDetection()
        {
            try
            {
                _stallDetectTimer?.Stop();
                _stallDetectTimer?.Dispose();
                _stallDetectTimer = null;
            }
            catch { }
        }
    }
}
