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
using RemotePlayServer.Infrastructure.Capture;
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

            // Per-track PC mode: also wait for all video PCs to reach connected state
            if (_perTrackPc && _streamer != null)
            {
                Logger.Info("[Protocol] Phase 3: perTrackPc=true — waiting for all video PCs to connect...");
                try
                {
                    var timeout = TimeSpan.FromSeconds(15);
                    var start = DateTime.UtcNow;
                    bool allVideoConnected = false;

                    while (DateTime.UtcNow - start < timeout)
                    {
                        var videoPcs = _streamer.VideoPcs;
                        int monitorCount = _streamer.MonitorCount;

                        if (videoPcs.Count >= monitorCount && monitorCount > 0)
                        {
                            bool allConnected = true;
                            foreach (var kvp in videoPcs)
                            {
                                if (kvp.Value.connectionState != SIPSorcery.Net.RTCPeerConnectionState.connected)
                                {
                                    allConnected = false;
                                    break;
                                }
                            }

                            if (allConnected)
                            {
                                allVideoConnected = true;
                                break;
                            }
                        }

                        await Task.Delay(100, _ct);
                    }

                    if (allVideoConnected)
                    {
                        Logger.Info("[Protocol] Phase 3: Tất cả video PCs đã kết nối!");
                    }
                    else
                    {
                        Logger.Warn("[Protocol] Phase 3: Timeout chờ video PCs kết nối — activating main PC fallback");
                        _streamer.ActivatePerTrackFallback();
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("[Protocol] Phase 3: Bị hủy khi chờ video PCs");
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
            // Internal fatal error CTS - linked to _ct (client disconnect)
            _fatalErrorCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);

            if (_streamer != null)
            {
                _streamer.OnFatalError += (reason) =>
                {
                    Logger.Error($"[Protocol] Fatal streamer error: {reason} - triggering cleanup");
                    _fatalErrorCts?.Cancel();
                };
            }

            // No barrier (single monitor) or no capture — activate immediately
            _streamer?.ActivatePhase3();

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

            while (_ws.State == WebSocketState.Open && !(_fatalErrorCts?.IsCancellationRequested ?? true))
            {
                try
                {
                    // Link to _fatalErrorCts so ReceiveAsync exits immediately when
                    // KeepAlive/StallDetect triggers fatal error (ICE/DTLS failure)
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                        _fatalErrorCts?.Token ?? _ct, _ct);
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


                    // Handle dedicated audio PeerConnection signaling
                    if (msgType == "audio_offer")
                    {
                        await HandleAudioOfferAsync(text);
                        continue;
                    }
                    if (msgType == "audio_candidate")
                    {
                        HandleAudioIceCandidate(text);
                        continue;
                    }

                    // Handle per-track video PC signaling (perTrackPc=true mode)
                    if (msgType == "video_answer" && _perTrackPc)
                    {
                        await HandleVideoAnswerAsync(text);
                        continue;
                    }
                    if (msgType == "video_candidate" && _perTrackPc)
                    {
                        HandleVideoIceCandidate(text);
                        continue;
                    }

                    // Handle per-track video PC reconnection request
                    if (msgType == "reconnect_video" && _perTrackPc && _streamer != null)
                    {
                        try
                        {
                            var doc = System.Text.Json.JsonDocument.Parse(text);
                            int monitorIndex = doc.RootElement.TryGetProperty("monitorIndex", out var mi) ? mi.GetInt32() : -1;
                            if (monitorIndex >= 0)
                            {
                                Logger.Info($"[Protocol] reconnect_video for monitor {monitorIndex} — recreating video PC and sending new offer");
                                await HandleReconnectVideoAsync(monitorIndex);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Protocol] reconnect_video error: {ex.Message}");
                        }
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
                        
                        // CRITICAL: Reset sync state on reconnect to ensure IDR is sent via DataChannel
                        // and stale frames are flushed from encoder pipeline.
                        _streamer?.ResetSyncState();

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

                        // CRITICAL: Reset sync state on reconnect
                        _streamer?.ResetSyncState();

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
                            await SendTextAsync(ackJson);
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
                                // Proactive keyframe on significant packet loss.
                                // H265 uses DataChannel (SCTP/reliable) — burst IDRs cause congestion death spiral.
                                // Only burst for H264/RTP (UDP/unreliable) where lost packets need IDR to recover.
                                if (feedback.PacketLossRate > 0.02f && _streamer.NegotiatedCodec != VideoCodec.H265)
                                    _streamer.RequestKeyframeBurst(-1, 1);

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

                    // Handle update_config from client for dynamic FPS/Bitrate/Resolution changes
                    if (msgType == "update_config")
                    {
                        try
                        {
                            var updateMsg = ProtocolMessageParser.Parse<UpdateConfigMessage>(text);
                            if (updateMsg != null && _streamer != null)
                            {
                                Logger.Info($"[Protocol] update_config received: fps={updateMsg.Fps}, resolutionHeight={updateMsg.ResolutionHeight}");

                                // Handle resolution change
                                if (updateMsg.ResolutionHeight.HasValue && _textureResizer != null)
                                {
                                    int newHeight = updateMsg.ResolutionHeight.Value;
                                    Logger.Info($"[Protocol] Dynamic resolution change requested: {_textureResizer.TargetHeight}p → {newHeight}p");
                                    
                                    // SAFE RESOLUTION CHANGE:
                                    // 1. Pause streamer to clear encoder pipeline
                                    _streamer?.Pause();
                                    
                                    // 2. Update resizer (recreates GPU scalers)
                                    _textureResizer.UpdateTargetHeight(newHeight);

                                    // 3. Force re-initialization of encoders BEFORE resume
                                    // This ensures old encoder handles are closed and new ones created.
                                    _streamer?.ForceReinitializeEncoders();

                                    // 4. Resume streamer
                                    _streamer?.Resume();

                                    Logger.Info($"[Protocol] Resolution changed to {newHeight}p, encoders re-initialized");
                                }

                                var (success, appliedFps, appliedResolutionHeight, message) = _streamer!.UpdateConfig(
                                    updateMsg.Fps,
                                    updateMsg.ResolutionHeight);

                                // Also update capture FPS if FPS was changed
                                if (updateMsg.Fps.HasValue && _capture != null)
                                {
                                    _capture.SetTargetFps(updateMsg.Fps.Value);
                                }

                                // Get current bitrate to send back in ack
                                var (_, currentBitrate, _) = _streamer.GetCurrentConfig();

                                // Send acknowledgment
                                var ack = new ConfigUpdatedMessage
                                {
                                    Fps = appliedFps,
                                    BitrateKbps = currentBitrate,
                                    ResolutionHeight = appliedResolutionHeight,
                                    Success = success,
                                    Message = message
                                };
                                await SendMessageAsync(ack);
                                Logger.Info($"[Protocol] config_updated sent: {message}, resolution={ack.ResolutionHeight}p");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Protocol] update_config error: {ex.Message}");
                        }
                        continue;
                    }

                    // ── codec_fallback: client H265 decoder failed, switch to H264 ──────────────
                    // Client sends this when H265StreamReceiver.OnDecoderFailed fires, meaning the
                    // Android device's hardware H265 decoder is not functional.
                    // Server responds with codec_switch ACK (encoder will switch on the next reconnect offer).
                    if (msgType == "codec_fallback")
                    {
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(text);
                            string fromCodec = json.RootElement.TryGetProperty("from", out var fp) ? fp.GetString() ?? "H265" : "H265";
                            string toCodec   = json.RootElement.TryGetProperty("to",   out var tp) ? tp.GetString() ?? "H264" : "H264";
                            string reason    = json.RootElement.TryGetProperty("reason", out var rp) ? rp.GetString() ?? "" : "";

                            Logger.Info($"[Protocol] codec_fallback received: {fromCodec} → {toCodec}, reason={reason}");

                            // Invalidate any in-flight offer processing from stale H265 auto-heal reconnects.
                            // The client will send a new offer with H264 preference after this ACK.
                            _offerGeneration++;
                            Logger.Info($"[Protocol] Offer generation bumped to {_offerGeneration} (stale H265 offers will be discarded)");

                            // Update server-side codec preference so the next reconnect offer
                            // is processed with H264 preference instead of H265.
                            if (toCodec.Equals("H264", StringComparison.OrdinalIgnoreCase))
                            {
                                _selectedCodec = "H264";
                                if (_streamer != null)
                                {
                                    _streamer.NegotiatedCodec = VideoCodec.H264;
                                    _streamer.ForceReinitializeEncoders();
                                }
                            }

                            // Pause encoder temporarily to avoid sending stale H265 frames
                            // while client is resetting (brief pause, not full stop)
                            _streamer?.Pause();
                            await Task.Delay(100, _ct);
                            _streamer?.Resume();

                            // ACK the fallback so client knows server is ready
                            var ackJson = $"{{\"type\":\"codec_switch\",\"codec\":\"{toCodec}\",\"reason\":\"client_fallback\"}}";
                            await SendTextAsync(ackJson);
                            Logger.Info($"[Protocol] codec_switch ACK sent → {toCodec}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Protocol] codec_fallback error: {ex.Message}");
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
                        // Resize texture to target resolution (default 1080p)
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

                    // Deferred send DISABLED — post-encode barrier removed for track independence.
                    // Each track now sends immediately after encoding (no cross-track blocking).
                    // Previously: barrier forced all tracks to wait for slowest encoder → stutter.
                    // H265: already sends via DataChannel immediately (unaffected).
                    // H264: sends via RTP immediately (minor jitter asymmetry acceptable vs stutter).
                    int activeMonitors = _capture.Monitors.Count(m => m.Device != null && m.Duplication != null);
                    Logger.Info($"[Protocol] Independent track mode: {activeMonitors} monitors, each track sends immediately after encoding");

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

        /// <summary>
        /// Handle per-track video PC reconnection: close old video PC, create new one, send offer.
        /// Called when client sends reconnect_video during Phase 3 streaming.
        /// </summary>
        private async Task HandleReconnectVideoAsync(int monitorIndex)
        {
            if (_streamer == null) return;

            try
            {
                // Close and recreate the video PC for this monitor
                _streamer.RecreateVideoPc(monitorIndex);

                // Generate and send new video offer for THIS monitor ONLY
                // CRITICAL: Do NOT call GetVideoPcOffersAsync() here — it regenerates offers for ALL
                // video PCs, which breaks the DTLS sessions of other connected video PCs.
                var offer = await _streamer.GetSingleVideoPcOfferAsync(monitorIndex);
                if (offer != null)
                {
                    var msg = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        type = "video_offer",
                        monitorIndex = offer.Value.monitorIndex,
                        sdp = offer.Value.offerSdp
                    });
                    await SendTextAsync(msg);
                    Logger.Info($"[Protocol] Sent reconnect video_offer for monitor {monitorIndex}");
                }
                else
                {
                    Logger.Warn($"[Protocol] No video offer generated for monitor {monitorIndex} reconnect");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] HandleReconnectVideoAsync error for monitor {monitorIndex}: {ex.Message}");
            }
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
                        Logger.Info("[KeepAlive] WebSocket closed. Stopping keepalive and triggering cleanup.");
                        _keepAliveTimer?.Stop();
                        
                        // Cancel fatal error CTS to unblock message loop so CleanupAsync runs
                        try { _fatalErrorCts?.Cancel(); } catch { }
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
                            Logger.Info($"[KeepAlive] Client not responding for {timeSinceLastPong / 1000:F1}s - forcing disconnect");
                            _keepAliveTimer?.Stop();

                            // Cancel fatal error CTS first to unblock message loop immediately
                            try { _fatalErrorCts?.Cancel(); } catch { }

                            // Then try graceful close (with short timeout since connection is likely dead)
                            try
                            {
                                using var cts = new CancellationTokenSource(500);
                                var closeTask = _ws.CloseOutputAsync(
                                    WebSocketCloseStatus.EndpointUnavailable,
                                    "Client not responding to keepalive",
                                    cts.Token);
                                
                                // Don't await forever if the socket is completely dead on the OS level
                                // Use Task.WhenAny to avoid AggregateException from Wait()
                                var delayTask = Task.Delay(500, cts.Token);
                                var completedTask = await Task.WhenAny(closeTask, delayTask);
                                
                                if (completedTask != closeTask)
                                {
                                    Logger.Info("[KeepAlive] CloseOutputAsync timed out or delayed. Aborting.");
                                    _ws.Abort();
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Info($"[KeepAlive] Close error (ignoring): {ex.Message}");
                                // Force abort the WebSocket if graceful close fails
                                try { _ws.Abort(); } catch { }
                            }
                            return;
                        }
                    }

                    // Send ping to client — if send fails, count as missed pong
                    try
                    {
                        await SendTextAsync("ping");
                    }
                    catch
                    {
                        _missedPongs++;
                    }
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
        private long _lastStallCheckFrameCount;              // Track frame production to distinguish static content from real stalls
        private DateTime _streamingStartTime;                // Grace period: don't fire stall detection during initial warmup
        private const int STALL_CHECK_INTERVAL_MS = 500;    // Check every 500ms (sufficient granularity)
        private const int STALL_THRESHOLD_MS = 1500;         // 1.5s without feedback = stall (tolerates WiFi jitter, still fast recovery)
        private const int STALL_WARMUP_MS = 5000;            // 5s grace period: H265 decoder init + SCTP ramp-up on Android
        private const int MAX_CONSECUTIVE_STALLS = 10;       // After 10 stalls, stop acting — let KeepAlive handle it

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
            _streamingStartTime = DateTime.UtcNow;

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

                    // Grace period: H265 decoder init + SCTP congestion window ramp-up
                    // causes natural feedback gaps during first few seconds.
                    // Cutting bitrate during this period makes things WORSE.
                    if ((DateTime.UtcNow - _streamingStartTime).TotalMilliseconds < STALL_WARMUP_MS) return;

                    var lastFeedback = new DateTime(Interlocked.Read(ref _lastClientFeedbackTicks));
                    var timeSinceLastFeedback = (DateTime.UtcNow - lastFeedback).TotalMilliseconds;
                    if (timeSinceLastFeedback > STALL_THRESHOLD_MS)
                    {
                        // Check if server is actually producing frames.
                        // If capture is idle (static screen), feedback gaps are expected — NOT a network stall.
                        // _frameCount is incremented by the capture callback for every captured frame.
                        var currentFrameCount = Interlocked.Read(ref _frameCount);
                        if (currentFrameCount == _lastStallCheckFrameCount)
                        {
                            // No new frames since last check → static content, not a stall
                            Interlocked.Exchange(ref _lastClientFeedbackTicks, DateTime.UtcNow.Ticks);
                            return;
                        }
                        _lastStallCheckFrameCount = currentFrameCount;

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

                        var (currentFps, currentBitrate, monitorCount) = streamer.GetCurrentConfig();

                        // CRITICAL: GetCurrentConfig returns TOTAL bitrate (per-encoder × monitorCount).
                        // ForceSetBitrate sets EACH encoder to the given value.
                        // We must calculate using per-encoder bitrate to avoid accidentally INCREASING it.
                        int perEncoderBitrate = monitorCount > 1 ? currentBitrate / monitorCount : currentBitrate;

                        // Escalating response based on consecutive stall count
                        int newBitrate;
                        string severity;
                        switch (_consecutiveStallCount)
                        {
                            case 1:
                                // First stall: 25% cut - could be momentary blip
                                newBitrate = Math.Max(2000, (int)(perEncoderBitrate * 0.75));
                                severity = "moderate (25% cut)";
                                break;
                            case 2:
                                // Second stall: 50% cut - clear sustained problem
                                newBitrate = Math.Max(2000, perEncoderBitrate / 2);
                                severity = "aggressive (50% cut)";
                                break;
                            case 3:
                                // Third stall: drop to minimum immediately
                                newBitrate = 2000;
                                severity = "emergency (minimum)";
                                break;
                            default:
                                newBitrate = 2000;
                                severity = "sustain (minimum)";
                                break;
                        }

                        // Safety: stall detection must NEVER increase bitrate.
                        if (newBitrate >= perEncoderBitrate && perEncoderBitrate > 2000)
                        {
                            newBitrate = Math.Max(2000, (int)(perEncoderBitrate * 0.75));
                            severity = $"clamped (was {severity}, would increase)";
                        }

                        Logger.Info($"[StallDetect] No feedback for {timeSinceLastFeedback / 1000:F1}s, stall #{_consecutiveStallCount} → {severity}: {perEncoderBitrate} → {newBitrate}kbps (per-encoder)");

                        // Bypass adaptive controller - directly set encoder bitrate
                        streamer.ForceSetBitrate(newBitrate);

                        // NEVER send keyframe burst for H265/DataChannel.
                        // H265 IDR frames are 200-400KB each. Burst of 3 × 2 tracks = 1.5-2.4MB
                        // dumped into SCTP buffer (reliable, ordered) → instant congestion death spiral.
                        // Intra refresh handles visual recovery gradually without bandwidth spikes.
                        // Only send keyframe burst for H264/RTP (UDP, unreliable — lost packets need IDR).
                        bool isDataChannel = streamer.NegotiatedCodec == VideoCodec.H265;
                        if (!isDataChannel && _consecutiveStallCount <= 3)
                            streamer.RequestKeyframeBurst(-1, 1);

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
            Logger.Info($"[StallDetect] Server-side stall detection started (1500ms threshold, {STALL_WARMUP_MS}ms warmup, frame-aware, escalating response)");
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
