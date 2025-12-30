#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using RemotePlayServer.Utils;
using RemotePlayServer.Encoding;

namespace RemotePlayServer.Protocol
{
    /// <summary>
    /// Connection phase states.
    /// </summary>
    public enum ConnectionPhase
    {
        Connected,
        Phase1_HardwareDetect,
        Phase1_SpeedTest,
        Phase1_WaitingProceed,
        Phase2_ApplyConfig,
        Phase2_IceExchange,
        Phase3_WaitingStart,
        Phase3_Streaming,
        Disconnecting,
        Error
    }

    /// <summary>
    /// Handles the 3-phase connection protocol.
    /// Phase 1: Hardware discovery + Speed test + Suggested config
    /// Phase 2: Display configuration + ICE/WebRTC setup
    /// Phase 3: Streaming
    /// </summary>
    public class PhaseProtocolHandler
    {
        private readonly Guid _clientId;
        private readonly WebSocket _ws;
        private readonly System.Net.IPAddress? _remoteIp;
        private readonly CancellationToken _ct;

        private ConnectionPhase _phase = ConnectionPhase.Connected;
        private HardwareInfo? _hardwareInfo;
        private EncoderInfo? _encoderInfo;
        private SpeedTestResult? _speedTestResult;
        private DisplayConfigMessage? _displayConfig;

        // Client codec capabilities (received in hardware_info_ack)
        private ClientCodecCapability? _clientCodecCapability;
        private string _selectedCodec = "H264";

        // Capture and streaming resources
        private PerMonitorCapture? _capture;
        private RemotePlayServer.Encoding.MultiPCStreamer? _streamer;
        private CancellationTokenSource? _captureCts;
        private Thread? _captureThread;

        // ICE handling
        private readonly Dictionary<int, List<string>> _pendingIce = new();
        private readonly HashSet<int> _answersReady = new();
        private readonly object _iceLock = new();

        // WebSocket send lock to prevent concurrent sends
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // Frame timing
        private readonly ConcurrentQueue<(long frameNum, long captureTime)> _frameTiming = new();
        private long _frameCount = 0;

        // Shared capture instance (like Program.cs pattern)
        private static PerMonitorCapture? _sharedCapture;
        private static readonly object _captureLock = new();

        // Monitor list
        private List<(IntPtr hmon, string name, int width, int height)> _monitors = new();

        // Monitor rects for cursor tracking (x, y, w, h) - populated when monitors are refreshed
        private List<(int x, int y, int w, int h)> _monitorRects = new();

        // Cursor tracking
        private CancellationTokenSource? _cursorCts;
        private int _lastCursorMonitor = -1;
        private float _lastCursorU = -1f;
        private float _lastCursorV = -1f;
        private bool _lastCursorVisible = false;

        // Server-side keep-alive for early disconnect detection
        private System.Timers.Timer? _keepAliveTimer;
        private DateTime _lastPongReceived = DateTime.UtcNow;
        private int _missedPongs = 0;
        private const int KEEPALIVE_INTERVAL_MS = 5000;  // Ping every 5s
        private const int MAX_MISSED_PONGS = 3;          // 15s without pong = dead

        // Wait for all monitors to connect before starting streaming
        private TaskCompletionSource<bool>? _allConnectedTcs;

        public PhaseProtocolHandler(
            Guid clientId,
            WebSocket ws,
            System.Net.IPAddress? remoteIp,
            CancellationToken ct)
        {
            _clientId = clientId;
            _ws = ws;
            _remoteIp = remoteIp;
            _ct = ct;
        }

        /// <summary>
        /// Main entry point for handling the client connection.
        /// </summary>
        public async Task HandleAsync()
        {
            Console.WriteLine($"[Protocol] Client {_clientId} connected from {_remoteIp} (v2 protocol)");

            try
            {
                // Phase 1: Hardware discovery and speed test
                await RunPhase1Async();

                // Phase 2: Configuration and ICE exchange
                await RunPhase2Async();

                // Phase 3: Streaming
                await RunPhase3Async();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[Protocol] Client {_clientId} cancelled");
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                Console.WriteLine($"[Protocol] Client {_clientId} disconnected prematurely");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] Client {_clientId} error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[Protocol] InnerException: {ex.InnerException.Message}");
                }
                Console.WriteLine($"[Protocol] StackTrace: {ex.StackTrace}");
                try { await SendErrorAsync(GetPhaseNumber(), "UNEXPECTED_ERROR", ex.Message); } catch { }
            }
            finally
            {
                await CleanupAsync();
            }
        }

        #region Phase 1: Hardware Discovery and Speed Test

        private async Task RunPhase1Async()
        {
            SetPhase(ConnectionPhase.Phase1_HardwareDetect);

            // Gather hardware info
            Console.WriteLine("[Protocol] Phase 1: Gathering hardware info...");
            _hardwareInfo = await HardwareInfoGatherer.GetHardwareInfoAsync();
            _encoderInfo = HardwareInfoGatherer.GetEncoderInfo();

            // Get current monitors
            _monitors = WgcInterop.ListMonitorsDXGI()
                .Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
            RefreshMonitorRects(); // Populate monitor rects for cursor tracking

            // Send hardware info to client
            var hwMsg = new HardwareInfoMessage
            {
                Device = new DeviceInfo
                {
                    Name = _hardwareInfo.DeviceName,
                    Processor = _hardwareInfo.Processor.Name,
                    Gpu = _hardwareInfo.Gpu.Name,
                    GpuVramGB = (int)Math.Round(_hardwareInfo.Gpu.VramMB / 1024.0),
                    RamGB = (int)Math.Round(_hardwareInfo.Ram.TotalMB / 1024.0),
                    Os = $"{_hardwareInfo.Os.Name} {_hardwareInfo.Os.Version}"
                },
                Encoder = _encoderInfo,
                Monitors = _monitors.Select((m, i) => new MonitorInfoDto
                {
                    Id = i,
                    Name = m.name,
                    Width = m.width,
                    Height = m.height,
                    IsVirtual = DisplayUtil.IsVirtualDisplay(m.name, m.hmon)
                }).ToList()
            };
            await SendMessageAsync(hwMsg);
            Console.WriteLine("[Protocol] Sent hardware info to client");

            // Wait for client to acknowledge hardware info
            Console.WriteLine("[Protocol] Waiting for hardware_info_ack...");
            await WaitForHardwareAckAsync();
            Console.WriteLine("[Protocol] Received hardware_info_ack");

            // NEW FLOW: Wait for Client to run speed test and send results
            SetPhase(ConnectionPhase.Phase1_SpeedTest);
            Console.WriteLine("[Protocol] Phase 1: Waiting for client speed test result...");

            // Wait for speedtest_result from Client (Client measures bandwidth/ping)
            try
            {
                _speedTestResult = await WaitForSpeedTestResultAsync();
                Console.WriteLine($"[Protocol] ✓ Received speedtest_result from client: {_speedTestResult.BandwidthMbps:F1}Mbps, {_speedTestResult.PingMs:F1}ms ping");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] ✗ Failed to receive speedtest_result: {ex.Message}");
                throw;
            }

            // Calculate and send suggested config based on Client's speed test results
            Console.WriteLine("[Protocol] Calculating suggested config...");
            var suggested = StreamingOptimizer.CalculateSuggestedConfig(_hardwareInfo, _encoderInfo, _speedTestResult);
            var sugMsg = new SuggestedConfigMessage
            {
                Monitors = suggested.Monitors,
                Resolution = new ResolutionDto { Width = suggested.ResolutionWidth, Height = suggested.ResolutionHeight },
                BitrateKbps = suggested.BitrateKbps,
                Fps = suggested.Fps,
                RefreshRate = suggested.RefreshRate,
                Reason = suggested.Reason,
                SelectedCodec = _selectedCodec
            };

            Console.WriteLine($"[Protocol] Sending suggested_config: {suggested.Monitors}x{suggested.ResolutionWidth}x{suggested.ResolutionHeight}@{suggested.Fps}fps, bitrate={suggested.BitrateKbps}kbps, codec={_selectedCodec}");
            await SendMessageAsync(sugMsg);
            Console.WriteLine("[Protocol] ✓ suggested_config sent successfully");

            // Wait for proceed
            SetPhase(ConnectionPhase.Phase1_WaitingProceed);
            Console.WriteLine("[Protocol] Phase 1: Waiting for client proceed...");
            await WaitForProceedAsync(2);
            Console.WriteLine("[Protocol] Phase 1 complete, proceeding to Phase 2");
        }

        /// <summary>
        /// Wait for speed test result from Client.
        /// Client runs the speed test and sends results to Server.
        /// </summary>
        private async Task<SpeedTestResult> WaitForSpeedTestResultAsync()
        {
            var buffer = new byte[128 * 1024];
            var ms = new System.IO.MemoryStream();

            while (_ws.State == WebSocketState.Open)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(120000); // 2 min timeout for speed test

                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new OperationCanceledException("Client closed connection");

                // Handle binary data (Client upload test - Client sends binary for Server to measure)
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    ms.Write(buffer, 0, result.Count);
                    continue;
                }

                // Text message
                ms.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                // Handle ping
                if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await SendTextAsync("pong");
                    continue;
                }

                var msgType = ProtocolMessageParser.GetMessageType(text);
                Console.WriteLine($"[Protocol] WaitForSpeedTest received: type={msgType ?? "null"}, len={text.Length}");

                // Debug: Log raw message content when type is null (parsing failed)
                if (msgType == null)
                {
                    Console.WriteLine($"[Protocol] DEBUG raw message: \"{text}\"");
                }

                // Handle speedtest_request from Client (Client wants Server to send data for download test)
                if (msgType == "speedtest_request")
                {
                    Console.WriteLine("[Protocol] Processing speedtest_request...");
                    var req = ProtocolMessageParser.Parse<SpeedTestRequestMessage>(text);
                    if (req != null)
                    {
                        await HandleSpeedTestRequestAsync(req.Direction, req.DurationMs);
                    }
                    Console.WriteLine("[Protocol] speedtest_request handled, continuing to wait...");
                    continue;
                }

                // Handle speedtest_result from Client (final results)
                if (msgType == "speedtest_result")
                {
                    Console.WriteLine("[Protocol] Processing speedtest_result...");
                    var resultMsg = ProtocolMessageParser.Parse<SpeedTestResultMessage>(text);
                    if (resultMsg != null)
                    {
                        Console.WriteLine($"[Protocol] speedtest_result parsed: {resultMsg.BandwidthMbps:F1}Mbps, {resultMsg.PingMs:F1}ms");
                        return new SpeedTestResult
                        {
                            BandwidthMbps = resultMsg.BandwidthMbps,
                            PingMs = resultMsg.PingMs,
                            JitterMs = resultMsg.JitterMs
                        };
                    }
                    else
                    {
                        Console.WriteLine("[Protocol] ✗ Failed to parse speedtest_result!");
                    }
                }
            }

            throw new OperationCanceledException("Did not receive speedtest_result");
        }

        /// <summary>
        /// Handle speed test request from Client.
        /// For download: Server sends binary data for Client to measure.
        /// For upload: Server prepares to receive binary data from Client.
        /// </summary>
        private async Task HandleSpeedTestRequestAsync(string direction, int durationMs)
        {
            Console.WriteLine($"[Protocol] Handling speedtest_request: direction={direction}, duration={durationMs}ms");

            if (direction == "download")
            {
                // Server sends binary data for Client to measure download speed
                // Use larger chunks (1MB) for higher throughput
                var chunkSize = 1024 * 1024; // 1MB chunks
                var chunk = new byte[chunkSize];
                new Random().NextBytes(chunk);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long bytesSent = 0;

                // Send chunks for the duration
                // Use ValueTask for better performance
                while (sw.ElapsedMilliseconds < durationMs && _ws.State == WebSocketState.Open)
                {
                    try
                    {
                        await _ws.SendAsync(new ArraySegment<byte>(chunk), WebSocketMessageType.Binary, true, _ct);
                        bytesSent += chunk.Length;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Protocol] Send error during speed test: {ex.Message}");
                        break;
                    }
                }

                sw.Stop();
                double mbps = bytesSent > 0 ? (bytesSent * 8.0) / (sw.ElapsedMilliseconds / 1000.0) / 1_000_000 : 0;

                // Send end marker
                var endMsg = $"{{\"type\":\"speedtest_end\",\"direction\":\"download\",\"totalBytes\":{bytesSent},\"durationMs\":{sw.ElapsedMilliseconds}}}";
                await SendTextAsync(endMsg);
                Console.WriteLine($"[Protocol] Download test complete: sent {bytesSent / (1024 * 1024)}MB in {sw.ElapsedMilliseconds}ms = {mbps:F1} Mbps");
            }
            else if (direction == "upload")
            {
                // Server will receive binary data from Client (measured by Client)
                // Just acknowledge that we're ready
                await SendTextAsync("{\"type\":\"speedtest_ready\",\"direction\":\"upload\"}");
                Console.WriteLine("[Protocol] Ready to receive upload test data from client");
            }
        }

        #endregion

        #region Phase 2: Configuration and ICE Exchange

        private async Task RunPhase2Async()
        {
            SetPhase(ConnectionPhase.Phase2_ApplyConfig);
            Console.WriteLine("[Protocol] Phase 2: Waiting for display config...");

            // Wait for display_config message
            _displayConfig = await WaitForDisplayConfigAsync();
            Console.WriteLine($"[Protocol] Received display config: {_displayConfig.Monitors}x{_displayConfig.Resolution.Width}x{_displayConfig.Resolution.Height}@{_displayConfig.Fps}fps");

            // Apply display configuration
            await ApplyDisplayConfigAsync(_displayConfig);

            // Refresh monitor list after VDD changes
            _monitors = WgcInterop.ListMonitorsDXGI()
                .Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
            RefreshMonitorRects(); // Refresh monitor rects for cursor tracking

            int actualMonitors = Math.Min(_displayConfig.Monitors, _monitors.Count);
            Console.WriteLine($"[Protocol] Available monitors: {_monitors.Count}, using: {actualMonitors}");

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
            Console.WriteLine("[Protocol] Sent config_complete, ready for ICE exchange");

            // ICE exchange phase - exits when proceed(3) is received
            SetPhase(ConnectionPhase.Phase2_IceExchange);
            await RunIceExchangeAsync();
            // Note: RunIceExchangeAsync now handles the proceed(3) message, no need to wait again
            Console.WriteLine("[Protocol] Phase 2 complete, proceeding to Phase 3");
        }

        private async Task ApplyDisplayConfigAsync(DisplayConfigMessage config)
        {
            await SendProgressAsync("vdd_setup", 0, "Checking display configuration...");

            bool configChanged = config.Monitors != DisplayConfig.MonitorCount ||
                                 config.Resolution.Width != DisplayConfig.MonitorWidth ||
                                 config.Resolution.Height != DisplayConfig.MonitorHeight ||
                                 config.Fps != DisplayConfig.StreamFps;

            if (configChanged)
            {
                Console.WriteLine($"[Protocol] Applying new display config: {config.Monitors} monitors @ {config.Resolution.Width}x{config.Resolution.Height}");

                // Stop existing capture if config changed
                lock (_captureLock)
                {
                    if (_sharedCapture != null)
                    {
                        Console.WriteLine("[Protocol] Stopping existing capture for reconfiguration...");
                        _sharedCapture.Stop();
                        _sharedCapture.Dispose();
                        _sharedCapture = null;
                    }
                }

                // Update config
                DisplayConfig.MonitorCount = config.Monitors;
                DisplayConfig.MonitorWidth = config.Resolution.Width;
                DisplayConfig.MonitorHeight = config.Resolution.Height;
                DisplayConfig.StreamFps = config.Fps;
                DisplayConfig.RefreshRate = config.RefreshRate;

                await SendProgressAsync("vdd_setup", 30, "Configuring virtual displays...");

                // Apply VDD and resolution changes
                await Task.Run(() =>
                {
                    StartupSteps.EnsureVddResolutionThenToggleDriver();
                    Thread.Sleep(2000);
                });

                await SendProgressAsync("topology", 60, "Setting up display topology...");

                await Task.Run(() =>
                {
                    StartupSteps.EnsureExtendDesktopWithVirtual();
                    Thread.Sleep(1000);
                });
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
            }

            // Create MultiPCStreamer with negotiated codec
            var codecEnum = _selectedCodec.Equals("H265", StringComparison.OrdinalIgnoreCase)
                ? VideoCodec.H265 : VideoCodec.H264;
            Console.WriteLine($"[Protocol] Creating MultiPCStreamer with codec={_selectedCodec} (enum={codecEnum})");
            _streamer = new RemotePlayServer.Encoding.MultiPCStreamer(
                actualMonitors, config.Fps, config.BitrateKbps, _capture.Device, codecEnum);

            // Wire up per-monitor devices for parallel encoding
            for (int i = 0; i < actualMonitors; i++)
            {
                var perMonDevice = _capture.GetDeviceForMonitor(i);
                if (perMonDevice != null)
                {
                    _streamer.SetDeviceForMonitor(i, perMonDevice);
                    Console.WriteLine($"[Protocol] Monitor {i}: Using dedicated D3D11 device");
                }
            }

            // ICE candidate forwarding
            _streamer.OnIceCandidate += async (monitorIndex, candidate) =>
            {
                try
                {
                    if (_ws.State != WebSocketState.Open) return;
                    var msg = new CandidateMessage { MonitorIndex = monitorIndex, Candidate = candidate };
                    await SendMessageAsync(msg);
                }
                catch { }
            };

            // Auto-recovery: Request reconnect when PC closed abnormally
            _streamer.OnMonitorNeedsReconnect += async (monitorIndex) =>
            {
                try
                {
                    if (_ws.State != WebSocketState.Open) return;
                    Console.WriteLine($"[Protocol] Requesting reconnect for monitor {monitorIndex}");
                    await SendTextAsync($"reconnect:{monitorIndex}");
                }
                catch { }
            };

            // Initialize TCS for waiting on all connections
            _allConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // ICE ready notification - sent when all PeerConnections have ICE connected
            _streamer.OnAllConnected += async () =>
            {
                try
                {
                    // Signal that all monitors are connected
                    _allConnectedTcs?.TrySetResult(true);

                    if (_ws.State != WebSocketState.Open) return;
                    Console.WriteLine($"[Protocol] All {actualMonitors} ICE connections ready, sending ice_ready");
                    var msg = new IceReadyMessage { MonitorCount = actualMonitors };
                    await SendMessageAsync(msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Protocol] Failed to send ice_ready: {ex.Message}");
                }
            };

            await SendProgressAsync("capture_init", 100, "Ready");
        }

        private async Task RunIceExchangeAsync()
        {
            Console.WriteLine("[Protocol] Starting ICE exchange...");

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

                    Console.WriteLine($"[Protocol] ICE RX: {text.Substring(0, Math.Min(80, text.Length))}...");

                    // Try to parse as JSON first
                    var msgType = ProtocolMessageParser.GetMessageType(text);
                    if (msgType != null)
                    {
                        Console.WriteLine($"[Protocol] ICE message type: {msgType}");
                        if (await HandleJsonMessageAsync(text, msgType))
                            break; // proceed received
                    }
                    else
                    {
                        // Handle legacy format (offer:N:sdp, candidate:N:...)
                        Console.WriteLine("[Protocol] ICE legacy format message");
                        await HandleLegacyMessageAsync(text);
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[Protocol] ICE exchange timed out");
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
                        ProcessIceCandidate(cand.MonitorIndex, cand.Candidate);
                    break;

                case "proceed":
                    var proceed = ProtocolMessageParser.Parse<ProceedMessage>(json);
                    if (proceed?.Phase == 3)
                        return true;
                    break;

                case "ping":
                    await SendMessageAsync(new PongMessage());
                    break;
            }
            return false;
        }

        private async Task HandleLegacyMessageAsync(string text)
        {
            // Ping/pong
            if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
            {
                await SendTextAsync("pong");
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
                    ProcessIceCandidate(monIdx, candStr);
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

        private async Task ProcessOfferAsync(int monitorIndex, string offerSdp)
        {
            if (_streamer == null) return;

            Console.WriteLine($"[Protocol] Received offer for monitor {monitorIndex}");

            // CRITICAL FIX: Clear ICE state for this monitor BEFORE processing new offer
            // This prevents race condition where client candidates arrive while new PC is being created
            // Without this, candidates go to AddIceCandidate immediately but PC isn't ready yet
            lock (_iceLock)
            {
                bool wasReady = _answersReady.Remove(monitorIndex);
                if (wasReady)
                {
                    Console.WriteLine($"[Protocol] Cleared _answersReady for m{monitorIndex} (reconnect case)");
                }
                // Also clear any pending candidates from previous connection
                if (_pendingIce.TryGetValue(monitorIndex, out var oldPending))
                {
                    if (oldPending.Count > 0)
                    {
                        Console.WriteLine($"[Protocol] Cleared {oldPending.Count} old pending candidates for m{monitorIndex}");
                        oldPending.Clear();
                    }
                }
            }

            try
            {
                var answerSdp = await _streamer.ProcessOfferAsync(
                    monitorIndex, offerSdp,
                    _displayConfig?.Resolution.Width ?? 1920,
                    _displayConfig?.Resolution.Height ?? 1080);

                // CRITICAL FIX: Extract embedded ICE candidates from answer SDP
                // SIPSorcery uses Vanilla ICE (all candidates embedded in SDP).
                // Unity WebRTC client hangs on SetRemoteDescription with embedded candidates.
                // Solution: Send answer SDP WITHOUT candidates, then send candidates separately.
                var (cleanSdp, embeddedCandidates) = ExtractIceCandidates(answerSdp);
                Console.WriteLine($"[Protocol] Extracted {embeddedCandidates.Count} embedded ICE candidates from answer");

                // Send answer (JSON format) - WITHOUT embedded candidates
                var answerMsg = new AnswerMessage { MonitorIndex = monitorIndex, Sdp = cleanSdp };
                var answerJson = ProtocolMessageParser.Serialize(answerMsg);
                Console.WriteLine($"[Protocol] Answer JSON for m{monitorIndex}: {answerJson.Substring(0, Math.Min(150, answerJson.Length))}...");
                await SendTextAsync(answerJson);
                Console.WriteLine($"[Protocol] Sent answer for monitor {monitorIndex}, len={answerJson.Length} bytes");

                // Send extracted ICE candidates separately (trickle ICE style)
                foreach (var candidate in embeddedCandidates)
                {
                    var candMsg = new CandidateMessage { MonitorIndex = monitorIndex, Candidate = candidate };
                    await SendMessageAsync(candMsg);
                    Console.WriteLine($"[Protocol] Sent extracted ICE candidate for m{monitorIndex}: {candidate.Substring(0, Math.Min(60, candidate.Length))}...");
                }

                lock (_iceLock)
                {
                    _answersReady.Add(monitorIndex);
                    // Process pending ICE
                    if (_pendingIce.TryGetValue(monitorIndex, out var pending))
                    {
                        foreach (var cand in pending)
                            _streamer.AddIceCandidate(monitorIndex, cand);
                        pending.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] ProcessOffer error m{monitorIndex}: {ex.Message}");
            }
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

        private void ProcessIceCandidate(int monitorIndex, string candidate)
        {
            if (_streamer == null) return;

            // Resolve mDNS if needed (reuse Program.cs logic)
            candidate = Program.MaybeResolveMdnsCandidateAsync(candidate).Result;
            candidate = Program.MaybeReplaceMdnsWithRemoteIp(candidate, _remoteIp);

            lock (_iceLock)
            {
                if (_answersReady.Contains(monitorIndex))
                {
                    _streamer.AddIceCandidate(monitorIndex, candidate);
                }
                else
                {
                    if (!_pendingIce.ContainsKey(monitorIndex))
                        _pendingIce[monitorIndex] = new List<string>();
                    _pendingIce[monitorIndex].Add(candidate);
                }
            }
        }

        #endregion

        #region Phase 3: Streaming

        private async Task RunPhase3Async()
        {
            SetPhase(ConnectionPhase.Phase3_WaitingStart);
            Console.WriteLine("[Protocol] Phase 3: Waiting for start_streaming command...");

            // Wait for start_streaming
            await WaitForStartStreamingAsync();

            // CRITICAL: Wait for ALL monitors to be ICE connected before starting streaming
            // This prevents network congestion from one monitor's stream interfering with
            // another monitor's ICE negotiation
            if (_allConnectedTcs != null)
            {
                Console.WriteLine("[Protocol] Phase 3: Waiting for all monitors to connect...");
                try
                {
                    // Wait up to 15 seconds for all monitors to connect
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var allConnectedTask = _allConnectedTcs.Task;
                    var completedTask = await Task.WhenAny(allConnectedTask, Task.Delay(Timeout.Infinite, cts.Token));

                    if (completedTask == allConnectedTask)
                    {
                        Console.WriteLine("[Protocol] Phase 3: All monitors connected!");
                    }
                    else
                    {
                        Console.WriteLine("[Protocol] Phase 3: Timeout waiting for all monitors, proceeding anyway");
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[Protocol] Phase 3: Timeout waiting for all monitors, proceeding anyway");
                }
            }

            SetPhase(ConnectionPhase.Phase3_Streaming);
            Console.WriteLine("[Protocol] Phase 3: Starting stream...");

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
            Console.WriteLine("[Protocol] Cursor tracking started");

            // Start server-side keep-alive for early disconnect detection
            StartKeepAlive();

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

                    // Handle ping/pong and stop_streaming
                    if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendTextAsync("pong");
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
                    if (msgType == "stop_streaming" || text.Equals("stop_streaming", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[Protocol] Received stop_streaming");
                        break;
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
                        Console.WriteLine("[Protocol] Received reconnect offer during streaming (JSON format)");
                        await HandleJsonMessageAsync(text, msgType);
                        continue;
                    }
                    if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[Protocol] Received reconnect offer during streaming (legacy format)");
                        await HandleLegacyMessageAsync(text);
                        continue;
                    }

                    // Handle end_of_candidates from client
                    if (msgType == "end_of_candidates")
                    {
                        Console.WriteLine("[Protocol] Received end_of_candidates during streaming");
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

                    // Handle skip_to_live request from client (for latency recovery)
                    // Client sends this when it detects accumulated delay > threshold
                    if (msgType == "skip_to_live")
                    {
                        Console.WriteLine("[Protocol] skip_to_live received - forcing keyframes for latency recovery");

                        // Force keyframe on all monitors to allow immediate recovery
                        _streamer?.RequestKeyframe(-1);

                        // Send acknowledgment with server timestamp
                        try
                        {
                            var ackJson = $"{{\"type\":\"skip_to_live_ack\",\"serverTime\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                            await _ws.SendAsync(
                                new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(ackJson)),
                                System.Net.WebSockets.WebSocketMessageType.Text,
                                true,
                                _ct);
                        }
                        catch { }
                        continue;
                    }
                }
                catch (OperationCanceledException) { break; }
            }

            // Stop keep-alive timer
            StopKeepAlive();

            // Stop cursor tracking
            StopCursorTracking();
            Console.WriteLine("[Protocol] Cursor tracking stopped");

            Console.WriteLine("[Protocol] Phase 3: Stream ended");
        }

        private void StartCaptureThread()
        {
            if (_capture == null || _streamer == null) return;

            _captureCts = new CancellationTokenSource();
            _captureThread = new Thread(() =>
            {
                try { RoInitialize(1); } catch { }
                try
                {
                    _capture.OnMonitorFrame += (monitorIndex, nv12Texture, w, h, timestamp) =>
                    {
                        _streamer?.PushTexture(monitorIndex, nv12Texture, w, h);
                        if (monitorIndex == 0)
                        {
                            var fn = Interlocked.Increment(ref _frameCount);
                            _frameTiming.Enqueue((fn, timestamp));
                            while (_frameTiming.Count > 30) _frameTiming.TryDequeue(out _);
                        }
                    };

                    _capture.Start();
                    _captureCts.Token.WaitHandle.WaitOne();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Protocol] Capture error: {ex.Message}");
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
                        if (_missedPongs >= MAX_MISSED_PONGS)
                        {
                            Console.WriteLine($"[KeepAlive] Client not responding for {timeSinceLastPong / 1000:F1}s, connection may be dead");
                            // Don't close WebSocket - let the main loop handle timeout
                            // This just provides early warning for logging
                        }
                    }

                    // Send ping to client
                    await SendTextAsync("ping");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[KeepAlive] Error: {ex.Message}");
                }
            };
            _keepAliveTimer.AutoReset = true;
            _keepAliveTimer.Start();
            Console.WriteLine("[KeepAlive] Server-side keep-alive started (5s interval)");
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

        #endregion

        #region Helper Methods

        private void SetPhase(ConnectionPhase phase)
        {
            _phase = phase;
            Console.WriteLine($"[Protocol] Phase changed to: {phase}");
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
                Console.WriteLine($"[Protocol] WaitForHardwareAck received: len={text.Length}, text={truncated}");

                // Handle ping
                if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await SendTextAsync("pong");
                    continue;
                }

                // Check for hardware_info_ack
                var msgType = ProtocolMessageParser.GetMessageType(text);
                Console.WriteLine($"[Protocol] WaitForHardwareAck msgType={msgType}");
                if (msgType == "hardware_info_ack")
                {
                    // Parse client codec capabilities
                    var ackMsg = ProtocolMessageParser.Parse<HardwareInfoAckMessage>(text);
                    if (ackMsg?.ClientCodecs != null)
                    {
                        _clientCodecCapability = ackMsg.ClientCodecs;
                        Console.WriteLine($"[Protocol] Client codec capabilities: HEVC={_clientCodecCapability.SupportsHevc}, " +
                                          $"preferred={_clientCodecCapability.PreferredCodec}, device={_clientCodecCapability.DeviceModel}");

                        // Negotiate codec: Use H.265 if both server and client support it
                        NegotiateCodec();
                    }
                    else
                    {
                        Console.WriteLine("[Protocol] No client codec capabilities in hardware_info_ack, using H.264");
                        _selectedCodec = "H264";
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// Negotiate codec based on server and client capabilities.
        /// </summary>
        private void NegotiateCodec()
        {
            // Check if both server and client support HEVC
            bool serverSupportsHevc = _encoderInfo?.SupportsHevc ?? false;
            bool clientSupportsHevc = _clientCodecCapability?.SupportsHevc ?? false;

            if (serverSupportsHevc && clientSupportsHevc)
            {
                _selectedCodec = "H265";
                Console.WriteLine("[Protocol] Codec negotiation: Both support HEVC -> selected H.265");
            }
            else
            {
                _selectedCodec = "H264";
                if (!serverSupportsHevc)
                    Console.WriteLine("[Protocol] Codec negotiation: Server doesn't support HEVC -> selected H.264");
                else if (!clientSupportsHevc)
                    Console.WriteLine("[Protocol] Codec negotiation: Client doesn't support HEVC -> selected H.264");
            }
        }

        // Buffer for display_config that might arrive before WaitForDisplayConfigAsync is called
        private DisplayConfigMessage? _bufferedDisplayConfig;

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

                Console.WriteLine($"[Protocol] WaitForProceed received: {text.Substring(0, Math.Min(100, text.Length))}...");

                // Handle ping
                if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await SendTextAsync("pong");
                    continue;
                }

                // Check for proceed
                var msgType = ProtocolMessageParser.GetMessageType(text);
                if (msgType == "proceed")
                {
                    var proceed = ProtocolMessageParser.Parse<ProceedMessage>(text);
                    Console.WriteLine($"[Protocol] Received proceed message, phase={proceed?.Phase}, expected={expectedPhase}");
                    if (proceed?.Phase == expectedPhase)
                        return;
                }
                else if (msgType == "display_config")
                {
                    // Buffer display_config that arrives early (client sends proceed + display_config back-to-back)
                    Console.WriteLine("[Protocol] Buffering early display_config");
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
                Console.WriteLine("[Protocol] Using buffered display_config");
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

                Console.WriteLine($"[Protocol] WaitForDisplayConfig received: {text.Substring(0, Math.Min(100, text.Length))}...");

                // Handle ping
                if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await SendTextAsync("pong");
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

                // Handle ping
                if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await SendTextAsync("pong");
                    continue;
                }

                var msgType = ProtocolMessageParser.GetMessageType(text);
                if (msgType == "start_streaming" || text.Equals("start_streaming", StringComparison.OrdinalIgnoreCase))
                    return;
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

            await _sendLock.WaitAsync(_ct);
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _ct);
            }
            finally
            {
                _sendLock.Release();
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

            // Stop keep-alive timer
            StopKeepAlive();

            // Stop capture
            try { _captureCts?.Cancel(); } catch { }
            try { _captureThread?.Join(500); } catch { }

            // Dispose streamer
            _streamer?.Dispose();
            _streamer = null;

            // Cleanup shared capture if we were the last user
            lock (_captureLock)
            {
                if (_sharedCapture != null)
                {
                    _sharedCapture.Stop();
                    _sharedCapture.Dispose();
                    _sharedCapture = null;

                    // Restore display settings
                    Console.WriteLine("[Protocol] Restoring display settings...");
                    try
                    {
                        DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(15));
                        Console.WriteLine("[Protocol] Display settings restored.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Protocol] Restore failed: {ex.Message}");
                    }
                }
            }

            // Close WebSocket
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }

            Console.WriteLine($"[Protocol] Client {_clientId} disconnected");
        }

        #endregion

        #region Native Methods

        [DllImport("combase.dll")]
        private static extern int RoInitialize(uint initType);

        // Cursor P/Invoke
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        private const int CURSOR_SHOWING = 0x00000001;

        /// <summary>
        /// Refresh monitor rects from DXGI for cursor position tracking.
        /// </summary>
        private void RefreshMonitorRects()
        {
            _monitorRects.Clear();
            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
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
                                _monitorRects.Add((rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] Failed to refresh monitor rects: {ex.Message}");
            }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CURSORINFO pci);

        #endregion

        #region Cursor Tracking

        /// <summary>
        /// Start cursor tracking task that sends position updates to client.
        /// </summary>
        private Task StartCursorTrackingTask()
        {
            _cursorCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            var ct = _cursorCts.Token;

            return Task.Run(async () =>
            {
                const int POLL_INTERVAL_MS = 16; // ~60Hz
                const float THRESHOLD = 0.001f; // Minimum UV change to send update

                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    try
                    {
                        var (monitorIndex, u, v, visible) = GetCursorPosition();

                        // Check if cursor changed significantly
                        bool changed = monitorIndex != _lastCursorMonitor ||
                                       visible != _lastCursorVisible ||
                                       (visible && (Math.Abs(u - _lastCursorU) > THRESHOLD || Math.Abs(v - _lastCursorV) > THRESHOLD));

                        if (changed)
                        {
                            _lastCursorMonitor = monitorIndex;
                            _lastCursorU = u;
                            _lastCursorV = v;
                            _lastCursorVisible = visible;

                            var msg = new CursorPositionMessage
                            {
                                MonitorIndex = monitorIndex,
                                U = u,
                                V = v,
                                Visible = visible
                            };
                            await SendMessageAsync(msg);
                        }

                        await Task.Delay(POLL_INTERVAL_MS, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* Ignore cursor errors */ }
                }
            }, ct);
        }

        /// <summary>
        /// Get current cursor position in UV coordinates relative to a monitor.
        /// </summary>
        private (int monitorIndex, float u, float v, bool visible) GetCursorPosition()
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci))
                return (-1, 0, 0, false);

            bool visible = (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero;
            if (!visible)
                return (-1, 0, 0, false);

            int cursorX = ci.ptScreenPos.X;
            int cursorY = ci.ptScreenPos.Y;

            // Find which monitor the cursor is on using _monitorRects
            if (_monitorRects.Count == 0)
                return (-1, 0, 0, false);

            for (int i = 0; i < _monitorRects.Count; i++)
            {
                var rect = _monitorRects[i];
                if (cursorX >= rect.x && cursorX < rect.x + rect.w &&
                    cursorY >= rect.y && cursorY < rect.y + rect.h)
                {
                    // Cursor is on this monitor - calculate UV (0-1)
                    float u = (float)(cursorX - rect.x) / rect.w;
                    float v = (float)(cursorY - rect.y) / rect.h;
                    return (i, u, v, true);
                }
            }

            return (-1, 0, 0, false);
        }

        /// <summary>
        /// Stop cursor tracking task.
        /// </summary>
        private void StopCursorTracking()
        {
            try { _cursorCts?.Cancel(); } catch { }
        }

        #endregion
    }
}
