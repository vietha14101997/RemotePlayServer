#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol
{
    public partial class PhaseProtocolHandler
    {
        private async Task RunPhase2Async()
        {
            SetPhase(ConnectionPhase.Phase2_ApplyConfig);
            Logger.Info("[Protocol] Phase 2: Waiting for display config...");

            // Wait for display_config message
            _displayConfig = await WaitForDisplayConfigAsync();
            Logger.Info($"[Protocol] Received display config: {_displayConfig.Monitors}x{_displayConfig.Resolution.Width}x{_displayConfig.Resolution.Height}@{_displayConfig.Fps}fps");

            // Apply display configuration
            await ApplyDisplayConfigAsync(_displayConfig);

            // Refresh monitor list after VDD changes
            _monitors = WgcInterop.ListMonitorsDXGI()
                .Select(m => (m.hmon, m.name, m.width, m.height)).ToList();

            // For ultrawide mode: filter to ONLY the virtual monitor
            if (_ultrawideVddName != null)
            {
                var vddMonitor = _monitors.FirstOrDefault(m =>
                    string.Equals(m.name, _ultrawideVddName, StringComparison.OrdinalIgnoreCase));
                if (vddMonitor.name != null)
                {
                    _monitors = new List<(IntPtr hmon, string name, int width, int height)> { vddMonitor };
                    Logger.Info($"[Protocol] Ultrawide: filtered to VDD monitor {vddMonitor.name} ({vddMonitor.width}x{vddMonitor.height})");
                }
                else
                {
                    Logger.Error($"[Protocol] Ultrawide: VDD monitor {_ultrawideVddName} not found in DXGI! Available: {string.Join(", ", _monitors.Select(m => m.name))}");
                }
            }
            else
            {
                // Standard mode: select and order monitors for VR streaming
                _monitors = SelectMonitorsForStreaming(_monitors, _displayConfig.Monitors);
            }

            // For ultrawide: wait for DXGI to reflect the new topology position (0,0)
            // After SetTopologyShowOnly, DXGI may briefly cache the old position (e.g. 1920,0)
            // where the VDD was before becoming the sole primary display.
            if (_ultrawideVddName != null && _monitors.Count > 0)
            {
                var vddMon = _monitors[0];
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool positionCorrect = false;
                while (sw.ElapsedMilliseconds < 3000)
                {
                    try
                    {
                        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                        for (uint ai = 0; !positionCorrect; ai++)
                        {
                            if (factory.EnumAdapters1(ai, out var adapter).Failure) break;
                            using (adapter)
                            {
                                for (uint oi = 0; ; oi++)
                                {
                                    if (adapter.EnumOutputs(oi, out var output).Failure) break;
                                    using (output)
                                    {
                                        var desc = output.Description;
                                        if (desc.Monitor == vddMon.hmon)
                                        {
                                            var rect = desc.DesktopCoordinates;
                                            if (rect.Left == 0 && rect.Top == 0)
                                            {
                                                positionCorrect = true;
                                                Logger.Info($"[Protocol] DXGI position confirmed: VDD at (0,0) after {sw.ElapsedMilliseconds}ms");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    if (positionCorrect) break;
                    Thread.Sleep(200);
                }

                if (!positionCorrect)
                {
                    Logger.Warn("[Protocol] DXGI position not updated to (0,0) after 3s — cursor tracking may be offset");
                }
            }

            RefreshMonitorRects(); // Refresh monitor rects for cursor tracking

            int actualMonitors = Math.Min(_displayConfig.Monitors, _monitors.Count);
            Logger.Info($"[Protocol] Available monitors: {_monitors.Count}, using: {actualMonitors}");

            // Create capture and streamer
            await CreateCaptureAndStreamerAsync(actualMonitors, _displayConfig);

            // Send config_complete
            var completeMsg = new ConfigCompleteMessage
            {
                Monitors = _monitors.Take(actualMonitors).Select((m, i) => new MonitorInfoDto
                {
                    Id = i,
                    Name = m.name,
                    Width = m.width,
                    Height = m.height,
                    IsVirtual = DisplayUtil.IsVirtualDisplay(m.name, m.hmon)
                }).ToList(),
                CaptureReady = true
            };
            await SendMessageAsync(completeMsg);
            Logger.Info("[Protocol] Sent config_complete, ready for ICE exchange");

            // ICE exchange phase - exits when proceed(3) is received
            SetPhase(ConnectionPhase.Phase2_IceExchange);
            await RunIceExchangeAsync();
            // Note: RunIceExchangeAsync now handles the proceed(3) message, no need to wait again
            Logger.Info("[Protocol] Phase 2 complete, proceeding to Phase 3");
        }

        /// <summary>
        /// Ultrawide display setup using complete 7-step flow:
        /// 1. Ensure VDD off → 2. Snapshot physical → 3. Create VDD + find virtual →
        /// 4. Set primary → 5. Set 125% scale → 6. Disconnect physical → 7. Verify resolution
        /// All steps are handled inside SetupUltrawideVirtualMonitor.
        /// </summary>
        private async Task ApplyUltrawideDisplayAsync(DisplayConfigMessage config)
        {
            Logger.Info($"[Protocol] Ultrawide mode: {DisplayConfig.MonitorType}");

            StopExistingCapture();

            int width = DisplayConfig.MonitorType == "ultrawide" ? 2560 : 3840;
            int height = 1080;
            int hz = config.RefreshRate > 0 ? config.RefreshRate : 60;

            DisplayConfig.MonitorCount = 1;
            DisplayConfig.StreamFps = config.Fps;
            DisplayConfig.RefreshRate = hz;

            // Complete 7-step ultrawide setup (VDD off → create → primary → scale → ShowOnly → verify)
            await SendProgressAsync("vdd_setup", 20, $"Creating ultrawide virtual display ({width}x{height})...");
            string? vddName = await Task.Run(() =>
                VirtualDisplayManager.SetupUltrawideVirtualMonitor(width, height, hz));

            if (vddName == null)
            {
                Logger.Error("[Protocol] Failed to create ultrawide virtual monitor!");
                await SendProgressAsync("vdd_setup", 30, "Virtual display creation failed, using standard mode...");
                DisplayConfig.MonitorType = "standard";
                return;
            }

            // Store VDD name for monitor filtering in RunPhase2Async
            _ultrawideVddName = vddName;

            DisplayGuard.MarkShowOnlyActive(vddName);
            DisplayGuard.SpawnWatchdog();
            Logger.Info($"[Protocol] Show Only applied on {vddName}");
            _displayModified = true;
        }

        private void StopExistingCapture()
        {
            lock (_captureLock)
            {
                if (_sharedCapture != null)
                {
                    Logger.Info("[Protocol] Stopping existing capture...");
                    _sharedCapture.Stop();
                    _sharedCapture.Dispose();
                    _sharedCapture = null;
                }
            }
        }

        private async Task ApplyDisplayConfigAsync(DisplayConfigMessage config)
        {
            await SendProgressAsync("vdd_setup", 0, "Checking display configuration...");

            // Store monitor type in runtime config
            DisplayConfig.MonitorType = config.MonitorType ?? "standard";
            bool isUltrawide = DisplayConfig.MonitorType == "ultrawide" || DisplayConfig.MonitorType == "super_ultrawide";

            if (isUltrawide)
            {
                await ApplyUltrawideDisplayAsync(config);
            }
            else
            {
                // ── Standard flow: multi-monitor extend ──
                // NOTE: We IGNORE client's resolution - server captures at NATIVE resolution
                // Server may later resize before encoding (to max 1440x810), but display stays native
                bool monitorCountChanged = config.Monitors != DisplayConfig.MonitorCount;
                bool fpsChanged = config.Fps != DisplayConfig.StreamFps;

                if (monitorCountChanged || fpsChanged)
                {
                    Logger.Info($"[Protocol] Applying new display config: {config.Monitors} monitors @ {config.Fps}fps (keeping native resolution)");
                    StopExistingCapture();

                    // Update config - DO NOT change resolution, keep native
                    DisplayConfig.MonitorCount = config.Monitors;
                    DisplayConfig.StreamFps = config.Fps;
                    DisplayConfig.RefreshRate = config.RefreshRate;

                    // Snapshot physical monitors first to decide if VDD is needed
                    VirtualDisplayManager.SnapshotPhysicalMonitors();
                    int physicalCount = VirtualDisplayManager.PhysicalMonitorNames.Count;
                    int requested = config.Monitors;

                    if (physicalCount < requested)
                    {
                        // Need VDD: create virtual monitors to fill the gap
                        Logger.Info($"[Protocol] Physical monitors ({physicalCount}) < requested ({requested}), creating VDD...");
                        await SendProgressAsync("vdd_setup", 30, "Configuring virtual displays...");

                        await Task.Run(() => VirtualDisplayManager.EnsureVddResolutionThenToggleDriver());
                        await Task.Delay(2000); // Allow Windows to stabilize VDD resolution

                        await SendProgressAsync("topology", 60, "Setting up display topology...");

                        await Task.Run(() => VirtualDisplayManager.EnsureExtendDesktopWithVirtual());
                        await Task.Delay(1000); // Allow Windows to apply topology change

                        DisplayGuard.SpawnWatchdog();
                        _displayModified = true;
                    }
                    else
                    {
                        // Enough physical monitors — no VDD needed, keep original layout
                        Logger.Info($"[Protocol] Physical monitors ({physicalCount}) >= requested ({requested}), no VDD needed");
                    }
                }
            }

            await SendProgressAsync("capture_init", 90, "Initializing capture...");
        }

        private async Task CreateCaptureAndStreamerAsync(int actualMonitors, DisplayConfigMessage config)
        {
            // Create capture
            lock (_captureLock)
            {
                if (_sharedCapture == null)
                {
                    _sharedCapture = new PerMonitorCapture(
                        _monitors.Take(actualMonitors).ToList(),
                        targetFps: config.Fps,
                        preferredGpu: config.PreferGpu);
                }
                _capture = _sharedCapture;

                // Subscribe to DXGI cursor updates from PerMonitorCapture
                _capture.OnCursorUpdate += HandleDxgiCursorUpdate;
            }

            // Initialize TCS for waiting on all connections
            _allConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Create SIPSorcery streamer with negotiated codec
            // BitrateKbps from client is TOTAL for all monitors - divide by count for per-encoder bitrate
            var negotiatedCodec = ParseVideoCodec(_selectedCodec);
            int perEncoderBitrate = config.BitrateKbps / Math.Max(1, actualMonitors);
            Logger.Info($"[Protocol] Creating SIPSorceryStreamer with codec={negotiatedCodec}, bitrate={perEncoderBitrate}kbps per encoder (total={config.BitrateKbps}kbps)");
            _streamer = new SIPSorceryStreamer(
                actualMonitors, config.Fps, perEncoderBitrate, _capture.Device, negotiatedCodec);

            // Create texture resizer with target output height (default 1080p)
            _textureResizer = new TextureResizer(actualMonitors, TextureResizer.DEFAULT_TARGET_HEIGHT);
            Logger.Info($"[Protocol] Created TextureResizer for {actualMonitors} monitors (targetHeight: {TextureResizer.DEFAULT_TARGET_HEIGHT}p, type: {DisplayConfig.MonitorType})");

            // Wire up per-monitor devices
            for (int i = 0; i < actualMonitors; i++)
            {
                var perMonDevice = _capture.GetDeviceForMonitor(i);
                if (perMonDevice != null)
                {
                    _streamer.SetDeviceForMonitor(i, perMonDevice);
                    Logger.Info($"[Protocol] Monitor {i}: Using dedicated D3D11 device");
                }
            }

            // ICE candidate forwarding
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

            // ICE ready notification
            _streamer.OnAllTracksReady += async () =>
            {
                try
                {
                    _allConnectedTcs?.TrySetResult(true);
                    if (_ws.State != WebSocketState.Open) return;
                    Logger.Info($"[Protocol] All {actualMonitors} tracks ready, sending ice_ready");

                    // Check if encoder supports BGRA mode (skip color conversion)
                    if (_streamer.AnyTrackRequiresBgraInput())
                    {
                        Logger.Info("[Protocol] Encoder supports BGRA mode - enabling zero-copy pipeline (no color conversion)");
                        _capture.UseBgraMode = true;
                    }

                    var msg = new IceReadyMessage { MonitorCount = actualMonitors };
                    await SendMessageAsync(msg);
                    Logger.Info("[Protocol] Starting early capture to prevent browser track timeout...");
                    StartCaptureThread();
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Protocol] Failed to send ice_ready: {ex.Message}");
                }
            };

            // Connection failed - auto-retry DTLS before requesting full client reconnect
            _streamer.OnConnectionFailed += async () =>
            {
                try
                {
                    if (_ws.State != WebSocketState.Open) return;
                    if (_dtlsRetrying) return; // Ignore events from old PC during retry

                    if (_dtlsRetryCount < MAX_DTLS_RETRIES && _lastOfferSdp != null)
                    {
                        _dtlsRetrying = true;
                        _dtlsRetryCount++;
                        Logger.Info($"[Protocol] DTLS failed, auto-retry {_dtlsRetryCount}/{MAX_DTLS_RETRIES} in 500ms...");

                        // Delay to exit PeerConnection event handler + allow socket cleanup
                        await Task.Delay(500);

                        // Reset DTLS completion gate
                        _allConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                        // Re-process cached offer (creates new PC with fresh certificate)
                        await ProcessSingleOfferAsync(_lastOfferSdp);

                        _dtlsRetrying = false;
                    }
                    else
                    {
                        // All DTLS retries exhausted — check if phase2 restarts are also exhausted
                        if (_phase2RestartCount >= MAX_PHASE2_RESTARTS)
                        {
                            Logger.Error($"[Protocol] DTLS failed after {MAX_DTLS_RETRIES} retries x {_phase2RestartCount} restarts — sending terminal error");
                            await SendTextAsync("{\"type\":\"connection_failed\",\"reason\":\"dtls_handshake_failed\",\"message\":\"WebRTC connection could not be established after multiple attempts. Please restart the app and try again.\"}");
                        }
                        else
                        {
                            Logger.Error($"[Protocol] DTLS failed after {MAX_DTLS_RETRIES} retries, requesting reconnect (restart {_phase2RestartCount + 1}/{MAX_PHASE2_RESTARTS})");
                            await SendTextAsync("{\"type\":\"reconnect_required\",\"reason\":\"dtls_failed\"}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _dtlsRetrying = false;
                    Logger.Error($"[Protocol] DTLS retry error: {ex.Message}");
                }
            };

            await SendProgressAsync("capture_init", 100, "Ready");
        }

        private async Task RunIceExchangeAsync()
        {
            Logger.Info("[Protocol] Starting ICE exchange...");

            // RX loop for offers and ICE candidates
            var buffer = new byte[128 * 1024];
            var ms = new System.IO.MemoryStream();

            while (_ws.State == WebSocketState.Open && _phase == ConnectionPhase.Phase2_IceExchange)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                    cts.CancelAfter(30000); // 30s timeout for ICE

                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    ms.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);

                    Logger.Info($"[Protocol] ICE RX: {text.Substring(0, Math.Min(80, text.Length))}...");

                    // Try to parse as JSON first
                    var msgType = ProtocolMessageParser.GetMessageType(text);
                    if (msgType != null)
                    {
                        Logger.Info($"[Protocol] ICE message type: {msgType}");
                        if (await HandleJsonMessageAsync(text, msgType))
                            break; // proceed received
                    }
                    else
                    {
                        // Handle legacy format (offer:N:sdp, candidate:N:...)
                        Logger.Info("[Protocol] ICE legacy format message");
                        await HandleLegacyMessageAsync(text);
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("[Protocol] ICE exchange timed out");
                    break;
                }
            }
        }

        private async Task<bool> HandleJsonMessageAsync(string json, string msgType)
        {
            switch (msgType)
            {
                case "offer":
                    var offer = ProtocolMessageParser.Parse<OfferMessage>(json);
                    if (offer != null)
                        await ProcessOfferAsync(offer.MonitorIndex, offer.Sdp);
                    break;

                case "candidate":
                    var cand = ProtocolMessageParser.Parse<CandidateMessage>(json);
                    if (cand != null)
                        await ProcessIceCandidateAsync(cand.MonitorIndex, cand.Candidate);
                    break;

                case "proceed":
                    var proceed = ProtocolMessageParser.Parse<ProceedMessage>(json);
                    if (proceed?.Phase == 3)
                    {
                        // Gate Phase 3 on DTLS completion to prevent the race condition where:
                        // 1. Client sends proceed phase 3
                        // 2. Server enters Phase 3, waits for start_streaming
                        // 3. But DTLS hasn't completed → ice_ready never sent → client never sends start_streaming
                        // 4. Client times out and disconnects
                        //
                        // By waiting here, we ensure DTLS is done before entering Phase 3,
                        // so ice_ready + start_streaming exchange happens reliably.
                        if (_allConnectedTcs != null && !_allConnectedTcs.Task.IsCompleted)
                        {
                            Logger.Info("[Protocol] Received proceed phase 3, waiting for DTLS to complete...");
                            try
                            {
                                using var dtlsCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(dtlsCts.Token, _ct);
                                var dtlsTask = _allConnectedTcs.Task;
                                var timeoutTask = Task.Delay(Timeout.Infinite, linkedCts.Token);
                                var completed = await Task.WhenAny(dtlsTask, timeoutTask);

                                if (completed == dtlsTask && dtlsTask.IsCompletedSuccessfully)
                                {
                                    Logger.Info("[Protocol] DTLS completed, proceeding to Phase 3");
                                }
                                else
                                {
                                    Logger.Error("[Protocol] DTLS timeout (10s) - requesting client to reconnect");
                                    try
                                    {
                                        await SendTextAsync("{\"type\":\"reconnect_required\",\"reason\":\"dtls_timeout\"}");
                                    }
                                    catch { }
                                    // Don't proceed to Phase 3 - let the ICE loop continue
                                    // The client should reconnect with a new offer
                                    break;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // Client disconnected or global cancellation
                                throw;
                            }
                        }
                        return true;
                    }
                    break;

                case "ping":
                    await SendMessageAsync(new PongMessage());
                    break;

                case "restart_phase2":
                    // Client is requesting full Phase 2 restart (ICE renegotiation)
                    _phase2RestartCount++;
                    if (_phase2RestartCount > MAX_PHASE2_RESTARTS)
                    {
                        Logger.Error($"[Protocol] Phase 2 restart limit reached ({_phase2RestartCount}/{MAX_PHASE2_RESTARTS}), rejecting restart");
                        await SendTextAsync("{\"type\":\"connection_failed\",\"reason\":\"max_restarts_exceeded\",\"message\":\"Connection failed after multiple reconnection attempts. Please restart the app.\"}");
                        return true; // Exit ICE loop
                    }
                    Logger.Info($"[Protocol] Client requested Phase 2 restart ({_phase2RestartCount}/{MAX_PHASE2_RESTARTS})");
                    await HandleRestartPhase2Async();
                    break;

                case "reconnect_ack":
                    // Client acknowledged successful reconnect for a monitor
                    HandleReconnectAck(json);
                    break;
            }
            return false;
        }

        private async Task HandleLegacyMessageAsync(string text)
        {
            // Ping/pong (with sequence support)
            if (await TryHandlePingAsync(text))
            {
                return;
            }

            // offer:N:sdp
            if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = text.Substring(6);
                var colonIdx = rest.IndexOf(':');
                if (colonIdx > 0 && int.TryParse(rest.Substring(0, colonIdx), out int monIdx))
                {
                    var offerSdp = rest.Substring(colonIdx + 1);
                    await ProcessOfferAsync(monIdx, offerSdp);
                }
                return;
            }

            // candidate:N:candidate
            if (text.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = text.Substring(10);
                var colonIdx = rest.IndexOf(':');
                if (colonIdx > 0 && int.TryParse(rest.Substring(0, colonIdx), out int monIdx))
                {
                    var candStr = rest.Substring(colonIdx + 1);
                    await ProcessIceCandidateAsync(monIdx, candStr);
                }
                return;
            }

            // proceed
            if (text.Trim().Equals("proceed", StringComparison.OrdinalIgnoreCase))
            {
                // Will be handled by wait loop
                return;
            }
        }

        /// <summary>
        /// Process a single SDP offer containing N m= sections (one per monitor).
        /// This is the new Single-PC Multi-Track flow.
        /// </summary>
        private async Task ProcessSingleOfferAsync(string offerSdp)
        {
            if (_streamer == null) return;

            // Cache offer for DTLS auto-retry
            _lastOfferSdp = offerSdp;
            _dtlsRetryCount = 0;

            Logger.Info("[Protocol] Received single offer for all monitors");

            // Clear answer ready state for new offer, but KEEP pending ICE candidates
            // ICE candidates may arrive BEFORE the offer due to trickle ICE timing
            lock (_iceLock)
            {
                _answersReady.Clear();
                // NOTE: Do NOT clear _pendingIce - candidates received before offer should be preserved
                var pendingCount = _pendingIce.TryGetValue(0, out var pending) ? pending.Count : 0;
                Logger.Info($"[Protocol] Cleared answer state, preserved {pendingCount} pending ICE candidates");
            }

            try
            {
                // Build dimensions list using RESIZED resolution
                // Each monitor has separate D3D11 device with dedicated GPU scaler
                // TextureResizer creates per-device scalers on demand
                var monitorCount = _displayConfig?.Monitors ?? 1;

                // Count m=video sections in offer to avoid creating more tracks than
                // the client negotiated. Client reconnect offers may have fewer video
                // tracks than the original (e.g., 1 video + 1 datachannel instead of 2 video + 1 datachannel).
                // Creating orphan tracks causes packets with unknown SSRCs that the client ignores.
                int offerVideoCount = CountVideoMLines(offerSdp);
                if (offerVideoCount > 0 && offerVideoCount < monitorCount)
                {
                    Logger.Info($"[Protocol] Offer has {offerVideoCount} m=video section(s) but {monitorCount} monitors configured. " +
                        $"Limiting tracks to {offerVideoCount} to match offer.");
                    monitorCount = offerVideoCount;
                }

                var targetHeight = TextureResizer.DEFAULT_TARGET_HEIGHT;
                var dimensions = new List<(int w, int h)>();
                for (int i = 0; i < monitorCount; i++)
                {
                    // Use resized resolution for encoder initialization
                    if (i < _monitors.Count)
                    {
                        var nativeW = _monitors[i].width;
                        var nativeH = _monitors[i].height;

                        // Calculate what TextureResizer will produce after scaling
                        var resizer = _textureResizer;
                        var (targetW, targetH) = resizer != null
                            ? resizer.CalculateTargetSize(nativeW, nativeH)
                            : CalculateFallbackDimensions(nativeW, nativeH, targetHeight);

                        dimensions.Add((targetW, targetH));
                        Logger.Info($"[Protocol] Monitor {i} native={nativeW}x{nativeH} -> encoder={targetW}x{targetH}");
                    }
                    else
                    {
                        // Fallback: use target height with 16:9 aspect ratio
                        var (fbW, fbH) = CalculateFallbackDimensions(1920, 1080, targetHeight);
                        dimensions.Add((fbW, fbH));
                        Logger.Info($"[Protocol] Monitor {i} using default encoder resolution: {fbW}x{fbH}");
                    }
                }

                // Parse offer to find codec PT (must match what the streamer uses)
                var codecPayloadType = ParseCodecPayloadType(offerSdp, _selectedCodec);
                Logger.Info($"[Protocol] Parsed {_selectedCodec} PT from offer: {codecPayloadType}");

                // Process offer with all dimensions at once
                var answerSdp = await _streamer.ProcessOfferAsync(offerSdp, dimensions);

                // Fix m-line order: SIPSorcery may reorder (audio before video) breaking strict WebRTC
                var reorderedSdp = ReorderAnswerToMatchOffer(answerSdp, offerSdp);

                // Filter SDP answer to only include selected codec
                var filteredSdp = FilterSdpForCodec(reorderedSdp, codecPayloadType, _selectedCodec);
                Logger.Info($"[Protocol] Filtered SDP from {answerSdp.Length} to {filteredSdp.Length} bytes");

                // Extract embedded ICE candidates from filtered SDP
                var (cleanSdp, embeddedCandidates) = ExtractIceCandidates(filteredSdp);
                Logger.Info($"[Protocol] Extracted {embeddedCandidates.Count} embedded ICE candidates from answer");

                // Send answer (JSON format) - single answer for all monitors
                var answerMsg = new AnswerMessage { MonitorIndex = 0, Sdp = cleanSdp };
                var answerJson = ProtocolMessageParser.Serialize(answerMsg);
                Logger.Info($"[Protocol] Answer JSON: {answerJson.Substring(0, Math.Min(150, answerJson.Length))}...");
                await SendTextAsync(answerJson);
                Logger.Info($"[Protocol] Sent single answer, len={answerJson.Length} bytes");

                // Send extracted ICE candidates separately (trickle ICE style)
                foreach (var candidate in embeddedCandidates)
                {
                    var candMsg = new CandidateMessage { MonitorIndex = 0, Candidate = candidate };
                    await SendMessageAsync(candMsg);
                    Logger.Info($"[Protocol] Sent extracted ICE candidate: {candidate.Substring(0, Math.Min(60, candidate.Length))}...");
                }

                lock (_iceLock)
                {
                    _answersReady.Add(0); // Mark single connection as ready
                    // Process pending ICE candidates that arrived before/during offer processing
                    if (_pendingIce.TryGetValue(0, out var pendingList) && pendingList.Count > 0)
                    {
                        Logger.Info($"[Protocol] Applying {pendingList.Count} pending ICE candidates");
                        foreach (var cand in pendingList)
                        {
                            _streamer.AddIceCandidate(cand, null);
                            Logger.Info($"[Protocol] Applied pending ICE: {cand.Substring(0, Math.Min(50, cand.Length))}...");
                        }
                        pendingList.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] ProcessSingleOffer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Legacy: Process offer for a specific monitor (backward compatibility).
        /// Redirects to single-offer flow.
        /// </summary>
        private async Task ProcessOfferAsync(int monitorIndex, string offerSdp)
        {
            // For backward compatibility, treat monitor 0 offer as a single offer
            // This allows gradual migration of clients
            if (monitorIndex == 0)
            {
                await ProcessSingleOfferAsync(offerSdp);
            }
            else
            {
                Logger.Info($"[Protocol] Warning: Received per-monitor offer for m{monitorIndex}, but Single-PC mode is active");
            }
        }

        /// <summary>
        /// Reorder answer SDP m-sections to match the offer's m-line order.
        /// SIPSorcery may reorder m-lines (e.g., putting audio before video) which causes
        /// "m-line order mismatch" errors in strict WebRTC implementations like Unity.WebRTC.
        /// Also remaps mid values to match the expected positions.
        /// </summary>
        private string ReorderAnswerToMatchOffer(string answerSdp, string offerSdp)
        {
            if (string.IsNullOrEmpty(answerSdp) || string.IsNullOrEmpty(offerSdp))
                return answerSdp;

            // Parse offer m-line types in order (e.g., ["m=video", "m=video", "m=audio"])
            var offerMTypes = new List<string>();
            foreach (var line in offerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("m="))
                    offerMTypes.Add(line.Split(' ')[0]);
            }

            // Parse answer into session header + m-line sections
            var answerLines = answerSdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sessionLines = new List<string>();
            var sections = new List<(string type, List<string> lines)>();
            List<string>? currentLines = null;
            string? currentType = null;

            foreach (var line in answerLines)
            {
                if (line.StartsWith("m="))
                {
                    if (currentLines != null)
                        sections.Add((currentType!, currentLines));
                    currentType = line.Split(' ')[0];
                    currentLines = new List<string> { line };
                }
                else if (currentLines != null)
                {
                    currentLines.Add(line);
                }
                else
                {
                    sessionLines.Add(line);
                }
            }
            if (currentLines != null)
                sections.Add((currentType!, currentLines));

            // Verify section count matches
            if (offerMTypes.Count != sections.Count)
            {
                Logger.Info($"[Protocol] Cannot reorder: offer has {offerMTypes.Count} m-sections, answer has {sections.Count}");
                return answerSdp;
            }

            // Check if already in correct order
            var answerMTypes = sections.Select(s => s.type).ToList();
            if (offerMTypes.SequenceEqual(answerMTypes))
                return answerSdp;

            // Group answer sections by media type for matching
            var answerByType = new Dictionary<string, Queue<(string type, List<string> lines)>>();
            foreach (var section in sections)
            {
                if (!answerByType.ContainsKey(section.type))
                    answerByType[section.type] = new Queue<(string, List<string>)>();
                answerByType[section.type].Enqueue(section);
            }

            // Reorder: for each offer m-type, pick next unused answer section of same type
            var reordered = new List<(string type, List<string> lines)>();
            int midIndex = 0;
            foreach (var offerType in offerMTypes)
            {
                if (answerByType.TryGetValue(offerType, out var queue) && queue.Count > 0)
                {
                    var section = queue.Dequeue();
                    // Remap mid value to match position index
                    var newMid = midIndex.ToString();
                    for (int j = 0; j < section.lines.Count; j++)
                    {
                        if (section.lines[j].StartsWith("a=mid:"))
                            section.lines[j] = $"a=mid:{newMid}";
                    }
                    reordered.Add(section);
                }
                midIndex++;
            }

            // Update BUNDLE group in session header
            var bundleMids = string.Join(" ", Enumerable.Range(0, reordered.Count));
            for (int i = 0; i < sessionLines.Count; i++)
            {
                if (sessionLines[i].StartsWith("a=group:BUNDLE"))
                    sessionLines[i] = $"a=group:BUNDLE {bundleMids}";
            }

            // Rebuild SDP
            var result = new List<string>(sessionLines);
            foreach (var (_, lines) in reordered)
                result.AddRange(lines);

            var newSdp = string.Join("\r\n", result);
            if (!newSdp.EndsWith("\r\n")) newSdp += "\r\n";

            Logger.Info($"[Protocol] Reordered answer m-lines: [{string.Join(", ", answerMTypes)}] -> [{string.Join(", ", reordered.Select(r => r.type))}]");
            return newSdp;
        }

        /// <summary>
        /// Filter SDP to only include selected codec per m=video section.
        /// Three-pass: 1) build PT→codec map, 2) rewrite each section with its actual codec PT,
        /// 3) inject missing rtpmap/fmtp for video sections SIPSorcery didn't generate.
        /// SIPSorcery assigns different PTs per monitor track (96, 97, ...).
        /// </summary>
        private string FilterSdpForCodec(string sdp, int payloadType, string targetCodec)
        {
            if (string.IsNullOrEmpty(sdp))
                return sdp;

            var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // Pass 1: Build PT → codec map from all rtpmap lines
            var ptCodecMap = new Dictionary<int, string>();
            foreach (var line in lines)
            {
                if (!line.StartsWith("a=rtpmap:")) continue;
                var pt = ExtractPayloadTypeFromLine(line);
                if (pt < 0) continue;
                // "a=rtpmap:96 H264/90000" → codec = "H264" (or H265, etc.)
                var colonIdx = line.IndexOf(':');
                var rest = line.Substring(colonIdx + 1);
                var spaceIdx = rest.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    var codecPart = rest.Substring(spaceIdx + 1);
                    var slashIdx = codecPart.IndexOf('/');
                    ptCodecMap[pt] = slashIdx > 0 ? codecPart.Substring(0, slashIdx) : codecPart;
                }
            }

            // Pass 2: Filter SDP, using actual codec PT per m=video section
            // Audio sections must preserve all codec attributes (rtpmap/fmtp/rtcp-fb)
            var filtered = new List<string>();
            int currentSectionPt = payloadType; // fallback to offer PT
            bool isAudioSection = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("m=video"))
                {
                    isAudioSection = false;
                    var parts = line.Split(' ');
                    if (parts.Length >= 4)
                    {
                        // Find the target codec PT among PTs listed in this m=video line
                        int targetedPt = -1;
                        for (int i = 3; i < parts.Length; i++)
                        {
                            if (int.TryParse(parts[i], out var pt) &&
                                ptCodecMap.TryGetValue(pt, out var codec) &&
                                codec.Equals(targetCodec, StringComparison.OrdinalIgnoreCase))
                            {
                                targetedPt = pt;
                                break;
                            }
                        }

                        // Fallback: if no target codec found in map, use first PT from line
                        if (targetedPt < 0 && int.TryParse(parts[3], out var firstPt))
                            targetedPt = firstPt;
                        if (targetedPt < 0)
                            targetedPt = payloadType;

                        currentSectionPt = targetedPt;
                        var protocol = parts[2].EndsWith("/SAVP") ? parts[2] + "F" : parts[2];
                        var newLine = $"m=video {parts[1]} {protocol} {currentSectionPt}";
                        filtered.Add(newLine);
                        Logger.Info($"[Protocol] SDP filtered m=video: {newLine} ({targetCodec} PT={currentSectionPt})");
                        continue;
                    }
                }

                if (line.StartsWith("m=audio"))
                {
                    isAudioSection = true;
                    filtered.Add(line);
                    continue;
                }

                // Reset on any other m= section (e.g., m=application)
                if (line.StartsWith("m=") && !line.StartsWith("m=video") && !line.StartsWith("m=audio"))
                {
                    isAudioSection = false;
                    filtered.Add(line);
                    continue;
                }

                // Audio sections: keep ALL attributes (rtpmap, fmtp, rtcp-fb, etc.)
                if (isAudioSection)
                {
                    filtered.Add(line);
                    continue;
                }

                // Keep only rtpmap for current section's codec PT
                if (line.StartsWith("a=rtpmap:"))
                {
                    var pt = ExtractPayloadTypeFromLine(line);
                    if (pt == currentSectionPt)
                    {
                        filtered.Add(line);
                        Logger.Info($"[Protocol] SDP kept: {line}");
                    }
                    continue;
                }

                if (line.StartsWith("a=fmtp:"))
                {
                    var pt = ExtractPayloadTypeFromLine(line);
                    if (pt == currentSectionPt)
                    {
                        string fixedLine = line;
                        // Update profile-level-id to match AMF encoder output (H264 only)
                        if (targetCodec.Equals("H264", StringComparison.OrdinalIgnoreCase))
                        {
                            fixedLine = line.Replace("profile-level-id=42e01f", "profile-level-id=420428")
                                            .Replace("profile-level-id=42001f", "profile-level-id=420428");
                        }
                        filtered.Add(fixedLine);
                        Logger.Info($"[Protocol] SDP kept: {fixedLine}");
                    }
                    continue;
                }

                if (line.StartsWith("a=rtcp-fb:"))
                {
                    var pt = ExtractPayloadTypeFromLine(line);
                    if (pt == currentSectionPt)
                    {
                        filtered.Add(line);
                    }
                    continue;
                }

                // Keep all other lines (session-level, ICE, DTLS, etc.)
                filtered.Add(line);
            }

            // Pass 3: Inject missing rtpmap/fmtp for video sections
            // SIPSorcery may only generate codec attributes for the last video track
            string? codecRtpmapSuffix = null; // e.g., "H265/90000" or "H264/90000"
            string? codecFmtpSuffix = null;   // e.g., "packetization-mode=1;..."
            foreach (var line in filtered)
            {
                if (codecRtpmapSuffix == null && line.StartsWith("a=rtpmap:") && line.Contains(targetCodec, StringComparison.OrdinalIgnoreCase))
                {
                    var spIdx = line.IndexOf(' ');
                    if (spIdx > 0) codecRtpmapSuffix = line.Substring(spIdx + 1);
                }
                if (codecFmtpSuffix == null && line.StartsWith("a=fmtp:"))
                {
                    var spIdx = line.IndexOf(' ');
                    if (spIdx > 0) codecFmtpSuffix = line.Substring(spIdx + 1);
                }
            }

            if (codecRtpmapSuffix != null)
            {
                // Identify video sections missing rtpmap and inject after a=mid: line
                var final = new List<string>();
                int vidPt = -1;
                bool injected = false;

                // First: scan each section to know which need injection
                var sectionNeedsInjection = new Dictionary<int, bool>();
                int scanPt = -1;
                bool scanHas = false;
                foreach (var line in filtered)
                {
                    if (line.StartsWith("m=video"))
                    {
                        if (scanPt >= 0) sectionNeedsInjection[scanPt] = !scanHas;
                        var parts = line.Split(' ');
                        scanPt = parts.Length >= 4 && int.TryParse(parts[3], out var p) ? p : -1;
                        scanHas = false;
                    }
                    else if (line.StartsWith("m="))
                    {
                        if (scanPt >= 0) sectionNeedsInjection[scanPt] = !scanHas;
                        scanPt = -1;
                    }
                    else if (line.StartsWith("a=rtpmap:") && scanPt >= 0)
                    {
                        scanHas = true;
                    }
                }
                if (scanPt >= 0) sectionNeedsInjection[scanPt] = !scanHas;

                // Second: rebuild with injections after a=mid: in sections that need it
                vidPt = -1;
                foreach (var line in filtered)
                {
                    if (line.StartsWith("m=video"))
                    {
                        var parts = line.Split(' ');
                        vidPt = parts.Length >= 4 && int.TryParse(parts[3], out var p) ? p : -1;
                        injected = false;
                    }
                    else if (line.StartsWith("m="))
                    {
                        vidPt = -1;
                    }

                    final.Add(line);

                    // Inject after a=mid: line for sections that need it
                    if (!injected && vidPt >= 0 && line.StartsWith("a=mid:")
                        && sectionNeedsInjection.TryGetValue(vidPt, out var needs) && needs)
                    {
                        final.Add($"a=rtpmap:{vidPt} {codecRtpmapSuffix}");
                        if (codecFmtpSuffix != null)
                            final.Add($"a=fmtp:{vidPt} {codecFmtpSuffix}");
                        Logger.Info($"[Protocol] Injected missing codec attrs for PT {vidPt}");
                        injected = true;
                    }
                }
                filtered = final;
            }

            var result = string.Join("\r\n", filtered);
            if (!result.EndsWith("\r\n")) result += "\r\n";
            return result;
        }

        private int ExtractPayloadTypeFromLine(string line)
        {
            // Extract PT from lines like "a=rtpmap:109 H264/90000" or "a=fmtp:109 ..."
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) return -1;

            var rest = line.Substring(colonIdx + 1);
            var spaceIdx = rest.IndexOf(' ');
            var ptStr = spaceIdx > 0 ? rest.Substring(0, spaceIdx) : rest;

            return int.TryParse(ptStr, out var pt) ? pt : -1;
        }

        /// <summary>
        /// Parse the H264 payload type from the offer SDP.
        /// Browser offers multiple H264 profiles - we prefer Constrained Baseline (42e01f) with packetization-mode=1.
        /// </summary>
        /// <summary>
        /// Count the number of m=video sections in an SDP string.
        /// Used to detect mismatch between expected monitors and offer video tracks.
        /// </summary>
        private static int CountVideoMLines(string sdp)
        {
            if (string.IsNullOrEmpty(sdp)) return 0;
            int count = 0;
            foreach (var line in sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("m=video", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Parse SDP to find the best payload type for the given codec
        /// </summary>
        private int ParseCodecPayloadType(string sdp, string codec)
        {
            if (string.IsNullOrEmpty(sdp)) return 96; // Fallback

            // Determine the rtpmap encoding name for the codec
            string codecRtpName;
            switch (codec?.ToUpperInvariant())
            {
                case "H265": codecRtpName = "H265/90000"; break;
                case "VP9":  codecRtpName = "VP9/90000"; break;
                case "VP8":  codecRtpName = "VP8/90000"; break;
                default:     codecRtpName = "H264/90000"; break;
            }

            var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // First pass: find codec payload types
            var codecPayloadTypes = new Dictionary<int, string>(); // PT -> fmtp line
            foreach (var line in lines)
            {
                if (line.StartsWith("a=rtpmap:") && line.Contains(codecRtpName))
                {
                    var parts = line.Substring(9).Split(' ');
                    if (parts.Length >= 1 && int.TryParse(parts[0], out int pt))
                    {
                        codecPayloadTypes[pt] = "";
                    }
                }
            }

            // Second pass: get fmtp for each PT
            foreach (var line in lines)
            {
                if (line.StartsWith("a=fmtp:"))
                {
                    var spaceIdx = line.IndexOf(' ', 7);
                    if (spaceIdx > 7)
                    {
                        var ptStr = line.Substring(7, spaceIdx - 7);
                        if (int.TryParse(ptStr, out int pt) && codecPayloadTypes.ContainsKey(pt))
                        {
                            codecPayloadTypes[pt] = line.Substring(spaceIdx + 1);
                        }
                    }
                }
            }

            // For H264, prefer specific profiles; for others, first match
            if (codec?.ToUpperInvariant() == "H264")
            {
                // Priority 1: Constrained Baseline (42e01f) with packetization-mode=1
                foreach (var kv in codecPayloadTypes)
                {
                    if (kv.Value.Contains("profile-level-id=42e01f") && kv.Value.Contains("packetization-mode=1"))
                        return kv.Key;
                }

                // Priority 2: Baseline (42001f) with packetization-mode=1
                foreach (var kv in codecPayloadTypes)
                {
                    if (kv.Value.Contains("profile-level-id=42001f") && kv.Value.Contains("packetization-mode=1"))
                        return kv.Key;
                }

                // Priority 3: Any H264 with packetization-mode=1
                foreach (var kv in codecPayloadTypes)
                {
                    if (kv.Value.Contains("packetization-mode=1"))
                        return kv.Key;
                }
            }

            // Return first match for any codec
            foreach (var kv in codecPayloadTypes)
            {
                return kv.Key;
            }

            return 96;
        }

        /// <summary>
        /// Extract ICE candidates from SDP and return clean SDP + list of candidates.
        /// </summary>
        private (string cleanSdp, List<string> candidates) ExtractIceCandidates(string sdp)
        {
            var candidates = new List<string>();
            if (string.IsNullOrEmpty(sdp))
                return (sdp, candidates);

            var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var filtered = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the candidate (without the "a=" prefix)
                    var candidate = line.Substring(2); // Remove "a="
                    candidates.Add(candidate);
                    continue; // Don't include in filtered SDP
                }

                filtered.Add(line);
            }

            var cleanSdp = string.Join("\r\n", filtered);
            if (!cleanSdp.EndsWith("\r\n")) cleanSdp += "\r\n";

            return (cleanSdp, candidates);
        }

        private async Task ProcessIceCandidateAsync(int monitorIndex, string candidate)
        {
            if (_streamer == null) return;

            // Resolve mDNS if needed
            candidate = await MdnsHelper.MaybeResolveMdnsCandidateAsync(candidate);
            candidate = MdnsHelper.MaybeReplaceMdnsWithRemoteIp(candidate, _remoteIp);

            lock (_iceLock)
            {
                // Single-PC mode: all candidates go to the same connection
                if (_answersReady.Contains(0))
                {
                    _streamer.AddIceCandidate(candidate, null);
                }
                else
                {
                    if (!_pendingIce.ContainsKey(0))
                        _pendingIce[0] = new List<string>();
                    _pendingIce[0].Add(candidate);
                }
            }
        }

        [DllImport("combase.dll")]
        private static extern int RoInitialize(uint initType);

        /// <summary>
        /// Refresh monitor rects from DXGI for cursor position tracking.
        /// Uses the same monitor order as _monitors to ensure index consistency with PerMonitorCapture.
        /// </summary>
        private void RefreshMonitorRects()
        {
            _monitorRects.Clear();
            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();

                // Build a lookup table of HMON -> DesktopCoordinates
                var hmonToRect = new Dictionary<IntPtr, (int x, int y, int w, int h)>();
                for (uint ai = 0; ; ai++)
                {
                    if (factory.EnumAdapters1(ai, out Vortice.DXGI.IDXGIAdapter1 adapter).Failure) break;
                    using (adapter)
                    {
                        for (uint oi = 0; ; oi++)
                        {
                            if (adapter.EnumOutputs(oi, out Vortice.DXGI.IDXGIOutput output).Failure) break;
                            using (output)
                            {
                                var desc = output.Description;
                                var rect = desc.DesktopCoordinates;
                                hmonToRect[desc.Monitor] = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                            }
                        }
                    }
                }

                // Populate _monitorRects in the same order as _monitors
                // This ensures index consistency with PerMonitorCapture which uses the same _monitors list
                for (int i = 0; i < _monitors.Count; i++)
                {
                    var mon = _monitors[i];
                    if (hmonToRect.TryGetValue(mon.hmon, out var rect))
                    {
                        _monitorRects.Add(rect);
                        Logger.Info($"[Protocol] Monitor {i} ({mon.name}): rect=({rect.x},{rect.y}) {rect.w}x{rect.h}");
                    }
                    else
                    {
                        // Monitor not found in DXGI - use fallback from stored dimensions
                        // Position unknown, assume (0,0) - cursor tracking may be inaccurate
                        Logger.Info($"[Protocol] Warning: Monitor {i} ({mon.name}) not found in DXGI outputs, using fallback rect (0,0) {mon.width}x{mon.height}");
                        _monitorRects.Add((0, 0, mon.width, mon.height));
                    }
                }

                Logger.Info($"[Protocol] Refreshed {_monitorRects.Count} monitor rects for cursor tracking (from {hmonToRect.Count} DXGI outputs)");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] Failed to refresh monitor rects: {ex.Message}");
            }
        }
        /// <summary>
        /// Select and order monitors for VR streaming based on the requested count.
        ///
        /// Rules:
        /// - If VDD was created (has virtual monitors): physical first (sorted by X), then virtual (sorted by X)
        /// - If all monitors are physical and count matches: sort by X (left → right)
        /// - If more physical than requested: pick primary + contiguous neighbors to the right,
        ///   filling from the left if not enough on the right. Ensures a continuous strip.
        /// </summary>
        private List<(IntPtr hmon, string name, int width, int height)> SelectMonitorsForStreaming(
            List<(IntPtr hmon, string name, int width, int height)> allMonitors, int requested)
        {
            var physicalNames = VirtualDisplayManager.PhysicalMonitorNames;
            bool hasVirtual = allMonitors.Any(m => !physicalNames.Contains(m.name));

            // Sort all monitors by desktop X coordinate
            var sorted = allMonitors
                .Select(m =>
                {
                    var (x, y, w, h, ok) = DisplayUtil.TryGetLayout(m.name);
                    bool isPhysical = physicalNames.Contains(m.name);
                    bool isPrimary = DisplayUtil.IsPrimary(m.name);
                    return (mon: m, x: ok ? x : int.MaxValue, isPhysical, isPrimary);
                })
                .OrderBy(m => m.x)
                .ToList();

            Logger.Info($"[Protocol] All monitors by X: {string.Join(", ", sorted.Select(m => $"{m.mon.name}({(m.isPhysical ? "P" : "V")},x={m.x}{(m.isPrimary ? ",PRI" : "")})"))}");

            if (hasVirtual)
            {
                // Has VDD monitors: physical first, then virtual, both by X
                var result = sorted.Where(m => m.isPhysical).Select(m => m.mon)
                    .Concat(sorted.Where(m => !m.isPhysical).Select(m => m.mon))
                    .ToList();
                Logger.Info($"[Protocol] Selected (physical→virtual): {string.Join(", ", result.Select(m => m.name))}");
                return result;
            }

            // All physical monitors
            int physicalCount = sorted.Count;

            if (physicalCount <= requested)
            {
                // Enough or exactly matching — use all, sorted by X
                var result = sorted.Select(m => m.mon).ToList();
                Logger.Info($"[Protocol] Selected (all physical by X): {string.Join(", ", result.Select(m => m.name))}");
                return result;
            }

            // More physical monitors than requested — select primary + contiguous neighbors
            int primaryIdx = sorted.FindIndex(m => m.isPrimary);
            if (primaryIdx < 0) primaryIdx = 0; // fallback: leftmost

            // Build contiguous strip of 'requested' monitors centered around primary
            // Strategy: start with primary, expand right first, then left
            int startIdx = primaryIdx;
            int endIdx = primaryIdx; // inclusive

            while (endIdx - startIdx + 1 < requested)
            {
                // Try expanding right first
                if (endIdx + 1 < physicalCount)
                {
                    endIdx++;
                }
                else if (startIdx - 1 >= 0)
                {
                    startIdx--;
                }
                else
                {
                    break; // shouldn't happen since physicalCount > requested
                }
            }

            var selected = sorted.Skip(startIdx).Take(endIdx - startIdx + 1).Select(m => m.mon).ToList();
            Logger.Info($"[Protocol] Selected (primary+neighbors [{startIdx}..{endIdx}]): {string.Join(", ", selected.Select(m => m.name))}");
            return selected;
        }

        /// <summary>
        /// Calculate fallback encoder dimensions for a given source and target height.
        /// Maintains aspect ratio and ensures even dimensions.
        /// </summary>
        private static (int w, int h) CalculateFallbackDimensions(int sourceW, int sourceH, int targetHeight)
        {
            if (sourceH == targetHeight)
                return (sourceW, sourceH);

            double scale = (double)targetHeight / sourceH;
            int w = (int)(sourceW * scale);
            int h = targetHeight;

            // Ensure even dimensions
            w = (w + 1) & ~1;
            h = (h + 1) & ~1;

            return (Math.Max(w, 2), Math.Max(h, 2));
        }
    }
}
