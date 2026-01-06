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
        private RemotePlayServer.Encoding.SIPSorceryStreamer? _streamer;
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
        private const int KEEPALIVE_INTERVAL_MS = 2000;  // Ping every 2s (synchronized with client)
        private const int MAX_MISSED_PONGS = 3;          // 6s without pong = dead (faster detection)

        // Wait for all monitors to connect before starting streaming
        private TaskCompletionSource<bool>? _allConnectedTcs;

        // Transport mode (USB Tethering vs WiFi)
        private readonly bool _isUsbTransport;

        // Track if display settings were modified (for cleanup)
        private bool _displayModified = false;

        /// <summary>
        /// Default bitrate for USB mode (higher due to stable bandwidth).
        /// </summary>
        private const int USB_DEFAULT_BITRATE_KBPS = 30000; // 30 Mbps for USB

        /// <summary>
        /// Default bitrate for WiFi mode (lower to handle variable bandwidth).
        /// </summary>
        private const int WIFI_DEFAULT_BITRATE_KBPS = 15000; // 15 Mbps for WiFi

        public PhaseProtocolHandler(
            Guid clientId,
            WebSocket ws,
            System.Net.IPAddress? remoteIp,
            CancellationToken ct,
            bool isUsbTransport = false)
        {
            _clientId = clientId;
            _ws = ws;
            _remoteIp = remoteIp;
            _ct = ct;
            _isUsbTransport = isUsbTransport;
        }

        /// <summary>
        /// Get default bitrate based on transport mode.
        /// </summary>
        public int GetDefaultBitrate() => _isUsbTransport ? USB_DEFAULT_BITRATE_KBPS : WIFI_DEFAULT_BITRATE_KBPS;

        /// <summary>
        /// Main entry point for handling the client connection.
        /// </summary>
        public async Task HandleAsync()
        {
            string transportStr = _isUsbTransport ? "USB Tethering" : "WiFi";
            Console.WriteLine($"[Protocol] Client {_clientId} connected from {_remoteIp} (v2 protocol, transport={transportStr})");

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

            // USB mode: Measure USB-specific latency and override bitrate
            int finalBitrate = suggested.BitrateKbps;
            string transportNote = "";
            UsbNetworkLatencyResult? usbLatency = null;
            
            if (_isUsbTransport)
            {
                // Measure USB network latency using ICMP ping to gateway
                Console.WriteLine("[Protocol] USB Mode: Measuring USB network latency...");
                usbLatency = await UsbNetworkLatency.MeasureAsync();
                
                if (usbLatency.IsUsbMode)
                {
                    Console.WriteLine($"[Protocol] ✓ USB Latency: {usbLatency.LatencyMs:F2}ms, Jitter: {usbLatency.JitterMs:F2}ms, Version: {usbLatency.InterfaceType}");
                    
                    // Override ping with USB-measured latency (more accurate than WebSocket ping)
                    if (usbLatency.LatencyMs > 0 && usbLatency.LatencyMs < _speedTestResult.PingMs)
                    {
                        Console.WriteLine($"[Protocol] Using USB latency {usbLatency.LatencyMs:F2}ms instead of WebSocket ping {_speedTestResult.PingMs:F2}ms");
                    }
                }
                
                finalBitrate = Math.Max(suggested.BitrateKbps, USB_DEFAULT_BITRATE_KBPS);
                transportNote = " [USB: High bitrate mode]";
            }

            // Determine connection type: USB takes priority over speedtest classification
            string connectionType = _isUsbTransport ? "USB" : _speedTestResult.ConnectionType;

            // Build NetworkInfoDto with USB-specific fields
            var networkInfo = new NetworkInfoDto
            {
                PingMs = _speedTestResult.PingMs,
                JitterMs = _speedTestResult.JitterMs,
                BandwidthMbps = _speedTestResult.BandwidthMbps,
                IsUsbMode = _isUsbTransport && usbLatency?.IsUsbMode == true,
                UsbLatencyMs = usbLatency?.LatencyMs ?? 0,
                UsbVersion = usbLatency?.InterfaceType,
                UsbEstimatedBandwidthMbps = usbLatency?.EstimatedBandwidthMbps ?? 0
            };

            var sugMsg = new SuggestedConfigMessage
            {
                Monitors = suggested.Monitors,
                Resolution = new ResolutionDto { Width = suggested.ResolutionWidth, Height = suggested.ResolutionHeight },
                BitrateKbps = finalBitrate,
                Fps = suggested.Fps,
                RefreshRate = suggested.RefreshRate,
                Reason = suggested.Reason + transportNote,
                SelectedCodec = _selectedCodec,
                ConnectionType = connectionType,
                NetworkInfo = networkInfo
            };

            Console.WriteLine($"[Protocol] Sending suggested_config: {suggested.Monitors}x{suggested.ResolutionWidth}x{suggested.ResolutionHeight}@{suggested.Fps}fps, bitrate={finalBitrate}kbps, codec={_selectedCodec}, transport={(_isUsbTransport ? "USB" : "WiFi")}");
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

                // Handle ping (with sequence support for accurate RTT)
                if (await TryHandlePingAsync(text))
                {
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
                // Use 4MB chunks for maximum throughput with sequential sends
                var chunkSize = 4 * 1024 * 1024; // 4MB chunks
                var chunk = new byte[chunkSize];
                new Random().NextBytes(chunk);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long bytesSent = 0;

                // Sequential sends - await each one for accurate bandwidth measurement
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

                // Mark display as modified for cleanup
                _displayModified = true;
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

            // Initialize TCS for waiting on all connections
            _allConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Create SIPSorcery streamer with negotiated codec
            var negotiatedCodec = ParseVideoCodec(_selectedCodec);
            Console.WriteLine($"[Protocol] Creating SIPSorceryStreamer with codec={negotiatedCodec}");
            _streamer = new RemotePlayServer.Encoding.SIPSorceryStreamer(
                actualMonitors, config.Fps, config.BitrateKbps, _capture.Device, negotiatedCodec);

            // Wire up per-monitor devices
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
                    Console.WriteLine($"[Protocol] All {actualMonitors} tracks ready, sending ice_ready");

                    // Check if encoder supports BGRA mode (skip color conversion)
                    if (_streamer.AnyTrackRequiresBgraInput())
                    {
                        Console.WriteLine("[Protocol] Encoder supports BGRA mode - enabling zero-copy pipeline (no color conversion)");
                        _capture.UseBgraMode = true;
                    }

                    var msg = new IceReadyMessage { MonitorCount = actualMonitors };
                    await SendMessageAsync(msg);
                    Console.WriteLine("[Protocol] Starting early capture to prevent browser track timeout...");
                    StartCaptureThread();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Protocol] Failed to send ice_ready: {ex.Message}");
                }
            };

            // Connection failed
            _streamer.OnConnectionFailed += async () =>
            {
                try
                {
                    if (_ws.State != WebSocketState.Open) return;
                    Console.WriteLine("[Protocol] Connection failed, requesting full reconnect");
                    await SendTextAsync("{\"type\":\"reconnect_required\"}");
                }
                catch { }
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

                case "restart_phase2":
                    // Client is requesting full Phase 2 restart (ICE renegotiation)
                    Console.WriteLine("[Protocol] Client requested Phase 2 restart");
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

        /// <summary>
        /// Process a single SDP offer containing N m= sections (one per monitor).
        /// This is the new Single-PC Multi-Track flow.
        /// </summary>
        private async Task ProcessSingleOfferAsync(string offerSdp)
        {
            if (_streamer == null) return;

            Console.WriteLine("[Protocol] Received single offer for all monitors");

            // Clear answer ready state for new offer, but KEEP pending ICE candidates
            // ICE candidates may arrive BEFORE the offer due to trickle ICE timing
            lock (_iceLock)
            {
                _answersReady.Clear();
                // NOTE: Do NOT clear _pendingIce - candidates received before offer should be preserved
                var pendingCount = _pendingIce.TryGetValue(0, out var pending) ? pending.Count : 0;
                Console.WriteLine($"[Protocol] Cleared answer state, preserved {pendingCount} pending ICE candidates");
            }

            try
            {
                // Build dimensions list from display config
                var monitorCount = _displayConfig?.Monitors ?? 1;
                var dimensions = new List<(int w, int h)>();
                for (int i = 0; i < monitorCount; i++)
                {
                    dimensions.Add((
                        _displayConfig?.Resolution.Width ?? 1920,
                        _displayConfig?.Resolution.Height ?? 1080
                    ));
                }

                // Parse offer to find H264 PT (must match what the streamer uses)
                var h264PayloadType = ParseH264PayloadType(offerSdp);
                Console.WriteLine($"[Protocol] Parsed H264 PT from offer: {h264PayloadType}");

                // Process offer with all dimensions at once
                var answerSdp = await _streamer.ProcessOfferAsync(offerSdp, dimensions);

                // CRITICAL: Filter SDP answer to only include selected codec
                // libdatachannel includes ALL codecs from offer, but browser picks FIRST in m= line
                var filteredSdp = FilterSdpForCodec(answerSdp, h264PayloadType);
                Console.WriteLine($"[Protocol] Filtered SDP from {answerSdp.Length} to {filteredSdp.Length} bytes");

                // Extract embedded ICE candidates from filtered SDP
                var (cleanSdp, embeddedCandidates) = ExtractIceCandidates(filteredSdp);
                Console.WriteLine($"[Protocol] Extracted {embeddedCandidates.Count} embedded ICE candidates from answer");

                // Send answer (JSON format) - single answer for all monitors
                var answerMsg = new AnswerMessage { MonitorIndex = 0, Sdp = cleanSdp };
                var answerJson = ProtocolMessageParser.Serialize(answerMsg);
                Console.WriteLine($"[Protocol] Answer JSON: {answerJson.Substring(0, Math.Min(150, answerJson.Length))}...");
                await SendTextAsync(answerJson);
                Console.WriteLine($"[Protocol] Sent single answer, len={answerJson.Length} bytes");

                // Send extracted ICE candidates separately (trickle ICE style)
                foreach (var candidate in embeddedCandidates)
                {
                    var candMsg = new CandidateMessage { MonitorIndex = 0, Candidate = candidate };
                    await SendMessageAsync(candMsg);
                    Console.WriteLine($"[Protocol] Sent extracted ICE candidate: {candidate.Substring(0, Math.Min(60, candidate.Length))}...");
                }

                lock (_iceLock)
                {
                    _answersReady.Add(0); // Mark single connection as ready
                    // Process pending ICE candidates that arrived before/during offer processing
                    if (_pendingIce.TryGetValue(0, out var pending) && pending.Count > 0)
                    {
                        Console.WriteLine($"[Protocol] Applying {pending.Count} pending ICE candidates");
                        foreach (var cand in pending)
                        {
                            _streamer.AddIceCandidate(cand, null);
                            Console.WriteLine($"[Protocol] Applied pending ICE: {cand.Substring(0, Math.Min(50, cand.Length))}...");
                        }
                        pending.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] ProcessSingleOffer error: {ex.Message}");
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
                Console.WriteLine($"[Protocol] Warning: Received per-monitor offer for m{monitorIndex}, but Single-PC mode is active");
            }
        }

        /// <summary>
        /// Filter SDP to only include the specified payload type.
        /// This is critical for WebRTC - browser uses FIRST codec in m= line.
        /// </summary>
        private string FilterSdpForCodec(string sdp, int payloadType)
        {
            if (string.IsNullOrEmpty(sdp))
                return sdp;

            var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var filtered = new List<string>();
            var ptStr = payloadType.ToString();

            foreach (var line in lines)
            {
                // Replace m=video line to only include our PT
                // Original: m=video 9 UDP/TLS/RTP/SAVP 39 41 43 96 103 107 109 ...
                // Fixed:    m=video 9 UDP/TLS/RTP/SAVPF 109
                if (line.StartsWith("m=video"))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 3)
                    {
                        // Keep port and protocol, replace with single PT
                        // Fix: Only add F if ending with exactly "SAVP" (not already "SAVPF")
                        var protocol = parts[2].EndsWith("/SAVP") ? parts[2] + "F" : parts[2];
                        var newLine = $"m=video {parts[1]} {protocol} {ptStr}";
                        filtered.Add(newLine);
                        Console.WriteLine($"[Protocol] SDP filtered m=video: {newLine}");
                        continue;
                    }
                }

                // Keep only rtpmap and fmtp for our PT (and rtx if paired)
                if (line.StartsWith("a=rtpmap:"))
                {
                    var pt = ExtractPayloadTypeFromLine(line);
                    if (pt == payloadType || pt == payloadType + 1) // Keep main PT and possibly RTX
                    {
                        filtered.Add(line);
                        Console.WriteLine($"[Protocol] SDP kept: {line}");
                    }
                    continue;
                }

                if (line.StartsWith("a=fmtp:"))
                {
                    var pt = ExtractPayloadTypeFromLine(line);
                    if (pt == payloadType || pt == payloadType + 1)
                    {
                        // CRITICAL: Update profile-level-id to match AMF encoder output
                        // AMF outputs: SPS header 67 42 04 28 = profile_idc=0x42, constraint=0x04, level=0x28 (4.0)
                        // Browser offered 42e01f (level 3.1) but encoder outputs 420428 (level 4.0)
                        var fixedLine = line.Replace("profile-level-id=42e01f", "profile-level-id=420428")
                                            .Replace("profile-level-id=42001f", "profile-level-id=420428");
                        filtered.Add(fixedLine);
                        Console.WriteLine($"[Protocol] SDP kept: {fixedLine}");
                    }
                    continue;
                }

                // Skip rtcp-fb lines for other codecs
                if (line.StartsWith("a=rtcp-fb:"))
                {
                    var pt = ExtractPayloadTypeFromLine(line);
                    if (pt == payloadType || pt == payloadType + 1)
                    {
                        filtered.Add(line);
                    }
                    continue;
                }

                // Keep all other lines (session-level, ICE, DTLS, etc.)
                filtered.Add(line);
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
        private int ParseH264PayloadType(string sdp)
        {
            if (string.IsNullOrEmpty(sdp)) return 96; // Fallback

            var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // First pass: find H264 payload types
            var h264PayloadTypes = new Dictionary<int, string>(); // PT -> fmtp line
            foreach (var line in lines)
            {
                if (line.StartsWith("a=rtpmap:") && line.Contains("H264/90000"))
                {
                    var parts = line.Substring(9).Split(' ');
                    if (parts.Length >= 1 && int.TryParse(parts[0], out int pt))
                    {
                        h264PayloadTypes[pt] = "";
                    }
                }
            }

            // Second pass: get fmtp for each H264 PT
            foreach (var line in lines)
            {
                if (line.StartsWith("a=fmtp:"))
                {
                    var spaceIdx = line.IndexOf(' ', 7);
                    if (spaceIdx > 7)
                    {
                        var ptStr = line.Substring(7, spaceIdx - 7);
                        if (int.TryParse(ptStr, out int pt) && h264PayloadTypes.ContainsKey(pt))
                        {
                            h264PayloadTypes[pt] = line.Substring(spaceIdx + 1);
                        }
                    }
                }
            }

            // Find best match
            // Priority 1: Constrained Baseline (42e01f) with packetization-mode=1
            foreach (var kv in h264PayloadTypes)
            {
                if (kv.Value.Contains("profile-level-id=42e01f") && kv.Value.Contains("packetization-mode=1"))
                    return kv.Key;
            }

            // Priority 2: Baseline (42001f) with packetization-mode=1
            foreach (var kv in h264PayloadTypes)
            {
                if (kv.Value.Contains("profile-level-id=42001f") && kv.Value.Contains("packetization-mode=1"))
                    return kv.Key;
            }

            // Priority 3: Any H264 with packetization-mode=1
            foreach (var kv in h264PayloadTypes)
            {
                if (kv.Value.Contains("packetization-mode=1"))
                    return kv.Key;
            }

            // Priority 4: First H264 found
            foreach (var kv in h264PayloadTypes)
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

        private void ProcessIceCandidate(int monitorIndex, string candidate)
        {
            if (_streamer == null) return;

            // Resolve mDNS if needed (reuse Program.cs logic)
            candidate = Program.MaybeResolveMdnsCandidateAsync(candidate).Result;
            candidate = Program.MaybeReplaceMdnsWithRemoteIp(candidate, _remoteIp);

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

                    // DEBUG: Log ALL Phase 3 messages for troubleshooting
                    Console.WriteLine($"[Protocol] Phase3 RX: type={msgType ?? "null"}, len={text.Length}, preview={text.Substring(0, Math.Min(100, text.Length))}");

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

                        Console.WriteLine($"[Protocol] request_keyframe received (monitor={monitorIndex}) - forcing IDR frame");
                        _streamer?.RequestKeyframe(monitorIndex);
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

                        Console.WriteLine($"[Protocol] skip_to_live received (monitor={monitorIndex}) - forcing keyframe for latency recovery");

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
                            Console.WriteLine($"[Protocol] fps_feedback error: {ex.Message}");
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
                                var bitrateResult = _streamer.ProcessQualityFeedback(feedback);
                                if (bitrateResult != null)
                                {
                                    await SendMessageAsync(bitrateResult);
                                    Console.WriteLine($"[Protocol] Bitrate adjusted: {bitrateResult.BitrateKbps}kbps - {bitrateResult.Reason}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Protocol] quality_feedback error: {ex.Message}");
                        }
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

            // Prevent duplicate start
            if (_captureThread != null && _captureThread.IsAlive)
            {
                Console.WriteLine("[Protocol] Capture thread already running, skipping start");
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
                        _streamer?.PushTexture(monitorIndex, nv12Texture, w, h);

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
                        _streamer?.PushBgraTexture(monitorIndex, bgraTexture, w, h);

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
                        Console.WriteLine($"[KeepAlive] Missed pong #{_missedPongs}, {timeSinceLastPong / 1000:F1}s since last response");

                        if (_missedPongs >= MAX_MISSED_PONGS)
                        {
                            Console.WriteLine($"[KeepAlive] Client not responding for {timeSinceLastPong / 1000:F1}s - closing connection");
                            _keepAliveTimer?.Stop();

                            // Actually close the connection instead of just warning
                            try
                            {
                                await _ws.CloseAsync(
                                    WebSocketCloseStatus.EndpointUnavailable,
                                    "Client not responding to keepalive",
                                    CancellationToken.None);
                            }
                            catch (Exception closeEx)
                            {
                                Console.WriteLine($"[KeepAlive] Error closing WebSocket: {closeEx.Message}");
                            }
                            return;
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

                // Handle ping (with sequence support)
                if (await TryHandlePingAsync(text))
                {
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
        /// Priority order: H264 (hardware) > H265 > VP9 > VP8
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

            Console.WriteLine($"[Protocol] Server supported codecs: [{string.Join(", ", serverCodecs)}]");

            // Get client supported codecs
            var clientCodecs = _clientCodecCapability?.SupportedCodecs ?? new[] { "H264" };
            Console.WriteLine($"[Protocol] Client supported codecs: [{string.Join(", ", clientCodecs)}]");

            // Find best mutual codec (priority: H264 first for hardware acceleration)
            string[] priority = { "H264", "H265", "VP9", "VP8" };

            foreach (var codec in priority)
            {
                if (serverCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase) &&
                    clientCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
                {
                    _selectedCodec = codec.ToUpperInvariant();
                    Console.WriteLine($"[Protocol] Codec negotiation: selected {_selectedCodec}");
                    return;
                }
            }

            // Default fallback to H264
            _selectedCodec = "H264";
            Console.WriteLine("[Protocol] Codec negotiation: no match found, defaulting to H264");
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
                    Console.WriteLine($"[Protocol] Encoder {encoderName}: {(available ? "available" : "not found")}");
                    return available;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] Error checking encoder {encoderName}: {ex.Message}");
                return false;
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
        /// Handle Phase 2 restart request from client.
        /// Stops current streaming, closes all peer connections, and restarts ICE negotiation.
        /// </summary>
        private async Task HandleRestartPhase2Async()
        {
            Console.WriteLine("[Protocol] Handling restart_phase2 request...");

            try
            {
                // 1. Stop current streaming if active
                if (_streamer != null)
                {
                    Console.WriteLine("[Protocol] Stopping current stream for restart...");
                    _streamer.Dispose();
                    _streamer = null;
                }

                // 2. Stop capture if active
                if (_sharedCapture != null)
                {
                    Console.WriteLine("[Protocol] Stopping capture for restart...");
                    _sharedCapture.Stop();
                    _sharedCapture.Dispose();
                    _sharedCapture = null;
                }

                // 3. Get current monitor count from display config
                int actualMonitors = _displayConfig?.Monitors ?? _monitors.Count;
                actualMonitors = Math.Min(actualMonitors, _monitors.Count);

                // 4. Send config_complete to client to trigger new ICE negotiation
                Console.WriteLine("[Protocol] Sending config_complete for Phase 2 restart");
                var completeMsg = new ConfigCompleteMessage
                {
                    Monitors = _monitors.Take(actualMonitors).Select((m, i) => new MonitorInfoDto
                    {
                        Id = i,
                        Name = m.name,
                        Width = m.width,
                        Height = m.height
                    }).ToList(),
                    CaptureReady = false // Will be ready after new ICE negotiation
                };
                await SendMessageAsync(completeMsg);

                Console.WriteLine("[Protocol] Phase 2 restart config_complete sent, waiting for ICE negotiation...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] Error handling restart_phase2: {ex.Message}");
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
                    Console.WriteLine($"[Protocol] Client acknowledged reconnect for monitor {monitorIndex}");

                    // Could track reconnect state here if needed
                    // _pendingReconnects.TryRemove(monitorIndex, out _);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] Error parsing reconnect_ack: {ex.Message}");
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
                }
            }

            // Restore display settings if they were modified
            // This runs even if _sharedCapture was already disposed (e.g., during reconnect attempts)
            if (_displayModified)
            {
                Console.WriteLine("[Protocol] Restoring display settings...");
                try
                {
                    DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(15));
                    Console.WriteLine("[Protocol] Display settings restored.");
                    _displayModified = false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Protocol] Restore failed: {ex.Message}");
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
