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
using Vortice.Direct3D11;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Infrastructure.Encoding;
using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol
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
        private SIPSorceryStreamer? _streamer;
        private CancellationTokenSource? _captureCts;
        private Thread? _captureThread;
        private TextureResizer? _textureResizer;

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

        // Cursor tracking (DXGI Desktop Duplication)
        private CancellationTokenSource? _cursorCts;
        private int _lastCursorMonitor = -1;
        private float _lastCursorU = -1f;
        private float _lastCursorV = -1f;
        private bool _lastCursorVisible = false;

        // Cursor image caching - track which cursor shape IDs have been sent to client
        private readonly HashSet<long> _sentCursorIds = new();

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
            Logger.Info($"[Protocol] Client {_clientId} connected from {_remoteIp} (v2 protocol, transport={transportStr})");

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
                Logger.Info($"[Protocol] Client {_clientId} cancelled");
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                Logger.Info($"[Protocol] Client {_clientId} disconnected prematurely");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] Client {_clientId} error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Logger.Error($"[Protocol] InnerException: {ex.InnerException.Message}");
                }
                Logger.Error($"[Protocol] StackTrace: {ex.StackTrace}");
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
            Logger.Info("[Protocol] Phase 1: Gathering hardware info...");
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
            Logger.Info("[Protocol] Sent hardware info to client");

            // Wait for client to acknowledge hardware info
            Logger.Info("[Protocol] Waiting for hardware_info_ack...");
            await WaitForHardwareAckAsync();
            Logger.Info("[Protocol] Received hardware_info_ack");

            // NEW FLOW: Wait for Client to run speed test and send results
            SetPhase(ConnectionPhase.Phase1_SpeedTest);
            Logger.Info("[Protocol] Phase 1: Waiting for client speed test result...");

            // Wait for speedtest_result from Client (Client measures bandwidth/ping)
            try
            {
                _speedTestResult = await WaitForSpeedTestResultAsync();
                Logger.Info($"[Protocol] ✓ Received speedtest_result from client: {_speedTestResult.BandwidthMbps:F1}Mbps, {_speedTestResult.PingMs:F1}ms ping");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Protocol] ✗ Failed to receive speedtest_result: {ex.Message}");
                throw;
            }

            // Calculate and send suggested config based on Client's speed test results
            Logger.Info("[Protocol] Calculating suggested config...");
            var suggested = StreamingOptimizer.CalculateSuggestedConfig(_hardwareInfo, _encoderInfo, _speedTestResult);

            // USB mode: Measure USB-specific latency and override bitrate
            int finalBitrate = suggested.BitrateKbps;
            string transportNote = "";
            UsbNetworkLatencyResult? usbLatency = null;
            
            if (_isUsbTransport)
            {
                // Measure USB network latency using ICMP ping to gateway
                Logger.Info("[Protocol] USB Mode: Measuring USB network latency...");
                usbLatency = await UsbNetworkLatency.MeasureAsync();
                
                if (usbLatency.IsUsbMode)
                {
                    Logger.Info($"[Protocol] ✓ USB Latency: {usbLatency.LatencyMs:F2}ms, Jitter: {usbLatency.JitterMs:F2}ms, Version: {usbLatency.InterfaceType}");
                    
                    // Override ping with USB-measured latency (more accurate than WebSocket ping)
                    if (usbLatency.LatencyMs > 0 && usbLatency.LatencyMs < _speedTestResult.PingMs)
                    {
                        Logger.Info($"[Protocol] Using USB latency {usbLatency.LatencyMs:F2}ms instead of WebSocket ping {_speedTestResult.PingMs:F2}ms");
                    }
                }
                
                finalBitrate = Math.Max(suggested.BitrateKbps, USB_DEFAULT_BITRATE_KBPS);
                transportNote = " [USB: High bitrate mode]";
            }

            // Determine connection type: USB takes priority over speedtest classification
            string connectionType = _isUsbTransport ? "USB" : _speedTestResult.ConnectionType;

            // Build NetworkInfoDto with USB-specific fields
            // In USB mode, use USB-measured jitter instead of WebSocket jitter
            var networkInfo = new NetworkInfoDto
            {
                PingMs = _speedTestResult.PingMs,
                JitterMs = (usbLatency?.IsUsbMode == true && usbLatency.JitterMs > 0) 
                    ? usbLatency.JitterMs  // Use USB ICMP jitter
                    : _speedTestResult.JitterMs,
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

            Logger.Info($"[Protocol] Sending suggested_config: {suggested.Monitors}x{suggested.ResolutionWidth}x{suggested.ResolutionHeight}@{suggested.Fps}fps, bitrate={finalBitrate}kbps, codec={_selectedCodec}, transport={(_isUsbTransport ? "USB" : "WiFi")}");
            await SendMessageAsync(sugMsg);
            Logger.Info("[Protocol] ✓ suggested_config sent successfully");

            // Wait for proceed
            SetPhase(ConnectionPhase.Phase1_WaitingProceed);
            Logger.Info("[Protocol] Phase 1: Waiting for client proceed...");
            await WaitForProceedAsync(2);
            Logger.Info("[Protocol] Phase 1 complete, proceeding to Phase 2");
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
                Logger.Info($"[Protocol] WaitForSpeedTest received: type={msgType ?? "null"}, len={text.Length}");

                // Debug: Log raw message content when type is null (parsing failed)
                if (msgType == null)
                {
                    Logger.Debug($"[Protocol] DEBUG raw message: \"{text}\"");
                }

                // Handle speedtest_request from Client (Client wants Server to send data for download test)
                if (msgType == "speedtest_request")
                {
                    Logger.Info("[Protocol] Processing speedtest_request...");
                    var req = ProtocolMessageParser.Parse<SpeedTestRequestMessage>(text);
                    if (req != null)
                    {
                        await HandleSpeedTestRequestAsync(req.Direction, req.DurationMs);
                    }
                    Logger.Info("[Protocol] speedtest_request handled, continuing to wait...");
                    continue;
                }

                // Handle speedtest_result from Client (final results)
                if (msgType == "speedtest_result")
                {
                    Logger.Info("[Protocol] Processing speedtest_result...");
                    var resultMsg = ProtocolMessageParser.Parse<SpeedTestResultMessage>(text);
                    if (resultMsg != null)
                    {
                        Logger.Info($"[Protocol] speedtest_result parsed: {resultMsg.BandwidthMbps:F1}Mbps, {resultMsg.PingMs:F1}ms");
                        return new SpeedTestResult
                        {
                            BandwidthMbps = resultMsg.BandwidthMbps,
                            PingMs = resultMsg.PingMs,
                            JitterMs = resultMsg.JitterMs
                        };
                    }
                    else
                    {
                        Logger.Error("[Protocol] ✗ Failed to parse speedtest_result!");
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
            Logger.Info($"[Protocol] Handling speedtest_request: direction={direction}, duration={durationMs}ms");

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
                        Logger.Error($"[Protocol] Send error during speed test: {ex.Message}");
                        break;
                    }
                }

                sw.Stop();
                double mbps = bytesSent > 0 ? (bytesSent * 8.0) / (sw.ElapsedMilliseconds / 1000.0) / 1_000_000 : 0;

                // Send end marker
                var endMsg = $"{{\"type\":\"speedtest_end\",\"direction\":\"download\",\"totalBytes\":{bytesSent},\"durationMs\":{sw.ElapsedMilliseconds}}}";
                await SendTextAsync(endMsg);
                Logger.Info($"[Protocol] Download test complete: sent {bytesSent / (1024 * 1024)}MB in {sw.ElapsedMilliseconds}ms = {mbps:F1} Mbps");
            }
            else if (direction == "upload")
            {
                // Server will receive binary data from Client (measured by Client)
                // Just acknowledge that we're ready
                await SendTextAsync("{\"type\":\"speedtest_ready\",\"direction\":\"upload\"}");
                Logger.Info("[Protocol] Ready to receive upload test data from client");
            }
        }

        #endregion

        #region Phase 2: Configuration and ICE Exchange

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

        private async Task ApplyDisplayConfigAsync(DisplayConfigMessage config)
        {
            await SendProgressAsync("vdd_setup", 0, "Checking display configuration...");

            // NOTE: We IGNORE client's resolution - server captures at NATIVE resolution
            // Server may later resize before encoding (to max 1440x810), but display stays native
            // Only check for monitor count and FPS changes
            bool monitorCountChanged = config.Monitors != DisplayConfig.MonitorCount;
            bool fpsChanged = config.Fps != DisplayConfig.StreamFps;

            if (monitorCountChanged || fpsChanged)
            {
                Logger.Info($"[Protocol] Applying new display config: {config.Monitors} monitors @ {config.Fps}fps (keeping native resolution)");

                // Stop existing capture if config changed
                lock (_captureLock)
                {
                    if (_sharedCapture != null)
                    {
                        Logger.Info("[Protocol] Stopping existing capture for reconfiguration...");
                        _sharedCapture.Stop();
                        _sharedCapture.Dispose();
                        _sharedCapture = null;
                    }
                }

                // Update config - DO NOT change resolution, keep native
                DisplayConfig.MonitorCount = config.Monitors;
                // Server captures at native resolution - no need to update from client
                DisplayConfig.StreamFps = config.Fps;
                DisplayConfig.RefreshRate = config.RefreshRate;

                await SendProgressAsync("vdd_setup", 30, "Configuring virtual displays...");

                // Apply VDD topology changes (only adds/removes virtual monitors, doesn't change resolution)
                await Task.Run(() =>
                {
                    VirtualDisplayManager.EnsureVddResolutionThenToggleDriver();
                    Thread.Sleep(2000);
                });

                await SendProgressAsync("topology", 60, "Setting up display topology...");

                await Task.Run(() =>
                {
                    VirtualDisplayManager.EnsureExtendDesktopWithVirtual();
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

            // Create texture resizer for max resolution enforcement (1440x810)
            // Create texture resizer (per-device scalers will be created on demand)
            _textureResizer = new TextureResizer(actualMonitors);
            Logger.Info($"[Protocol] Created TextureResizer for {actualMonitors} monitors (max: {TextureResizer.MaxWidth}x{TextureResizer.MaxHeight})");


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

            // Connection failed
            _streamer.OnConnectionFailed += async () =>
            {
                try
                {
                    if (_ws.State != WebSocketState.Open) return;
                    Logger.Error("[Protocol] Connection failed, requesting full reconnect");
                    await SendTextAsync("{\"type\":\"reconnect_required\"}");
                }
                catch { }
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
                    Logger.Info("[Protocol] Client requested Phase 2 restart");
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
                var dimensions = new List<(int w, int h)>();
                for (int i = 0; i < monitorCount; i++)
                {
                    // Use resized resolution for encoder initialization
                    if (i < _monitors.Count)
                    {
                        var nativeW = _monitors[i].width;
                        var nativeH = _monitors[i].height;
                        
                        // Calculate what TextureResizer will produce after scaling
                        var (targetW, targetH) = TextureResizer.CalculateTargetSize(nativeW, nativeH);
                        
                        dimensions.Add((targetW, targetH));
                        Logger.Info($"[Protocol] Monitor {i} native={nativeW}x{nativeH} -> encoder={targetW}x{targetH}");
                    }
                    else
                    {
                        // Fallback to max resize dimensions
                        dimensions.Add((TextureResizer.MaxWidth, TextureResizer.MaxHeight));
                        Logger.Info($"[Protocol] Monitor {i} using default encoder resolution: {TextureResizer.MaxWidth}x{TextureResizer.MaxHeight}");
                    }
                }

                // Parse offer to find H264 PT (must match what the streamer uses)
                var h264PayloadType = ParseH264PayloadType(offerSdp);
                Logger.Info($"[Protocol] Parsed H264 PT from offer: {h264PayloadType}");

                // Process offer with all dimensions at once
                var answerSdp = await _streamer.ProcessOfferAsync(offerSdp, dimensions);

                // Fix m-line order: SIPSorcery may reorder (audio before video) breaking strict WebRTC
                var reorderedSdp = ReorderAnswerToMatchOffer(answerSdp, offerSdp);

                // Filter SDP answer to only include selected codec
                var filteredSdp = FilterSdpForCodec(reorderedSdp, h264PayloadType);
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
                    if (_pendingIce.TryGetValue(0, out var pending) && pending.Count > 0)
                    {
                        Logger.Info($"[Protocol] Applying {pending.Count} pending ICE candidates");
                        foreach (var cand in pending)
                        {
                            _streamer.AddIceCandidate(cand, null);
                            Logger.Info($"[Protocol] Applied pending ICE: {cand.Substring(0, Math.Min(50, cand.Length))}...");
                        }
                        pending.Clear();
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
        /// Filter SDP to only include H264 codec per m=video section.
        /// Three-pass: 1) build PT→codec map, 2) rewrite each section with its actual H264 PT,
        /// 3) inject missing rtpmap/fmtp for video sections SIPSorcery didn't generate.
        /// SIPSorcery assigns different PTs per monitor track (96, 97, ...).
        /// </summary>
        private string FilterSdpForCodec(string sdp, int payloadType)
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
                // "a=rtpmap:96 H264/90000" → codec = "H264"
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

            // Pass 2: Filter SDP, using actual H264 PT per m=video section
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
                        // Find the H264 PT among PTs listed in this m=video line
                        int h264Pt = -1;
                        for (int i = 3; i < parts.Length; i++)
                        {
                            if (int.TryParse(parts[i], out var pt) &&
                                ptCodecMap.TryGetValue(pt, out var codec) &&
                                codec.Equals("H264", StringComparison.OrdinalIgnoreCase))
                            {
                                h264Pt = pt;
                                break;
                            }
                        }

                        // Fallback: if no H264 found in map, use first PT from line
                        if (h264Pt < 0 && int.TryParse(parts[3], out var firstPt))
                            h264Pt = firstPt;
                        if (h264Pt < 0)
                            h264Pt = payloadType;

                        currentSectionPt = h264Pt;
                        var protocol = parts[2].EndsWith("/SAVP") ? parts[2] + "F" : parts[2];
                        var newLine = $"m=video {parts[1]} {protocol} {currentSectionPt}";
                        filtered.Add(newLine);
                        Logger.Info($"[Protocol] SDP filtered m=video: {newLine} (H264 PT={currentSectionPt})");
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

                // Keep only rtpmap for current section's H264 PT
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
                        // Update profile-level-id to match AMF encoder output
                        var fixedLine = line.Replace("profile-level-id=42e01f", "profile-level-id=420428")
                                            .Replace("profile-level-id=42001f", "profile-level-id=420428");
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
            string? h264RtpmapSuffix = null; // e.g., "H264/90000"
            string? h264FmtpSuffix = null;   // e.g., "packetization-mode=1;..."
            foreach (var line in filtered)
            {
                if (h264RtpmapSuffix == null && line.StartsWith("a=rtpmap:") && line.Contains("H264"))
                {
                    var spIdx = line.IndexOf(' ');
                    if (spIdx > 0) h264RtpmapSuffix = line.Substring(spIdx + 1);
                }
                if (h264FmtpSuffix == null && line.StartsWith("a=fmtp:"))
                {
                    var spIdx = line.IndexOf(' ');
                    if (spIdx > 0) h264FmtpSuffix = line.Substring(spIdx + 1);
                }
            }

            if (h264RtpmapSuffix != null)
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
                        final.Add($"a=rtpmap:{vidPt} {h264RtpmapSuffix}");
                        if (h264FmtpSuffix != null)
                            final.Add($"a=fmtp:{vidPt} {h264FmtpSuffix}");
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

            // Resolve mDNS if needed
            candidate = MdnsHelper.MaybeResolveMdnsCandidateAsync(candidate).Result;
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

        #endregion

        #region Phase 3: Streaming

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

                    // Log important Phase 3 messages (skip frequent ones like quality_feedback, ping)
                    if (msgType != "quality_feedback" && msgType != "fps_feedback" && !text.StartsWith("ping:"))
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
                        await HandleJsonMessageAsync(text, msgType);
                        continue;
                    }
                    if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("[Protocol] Received reconnect offer during streaming (legacy format)");
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

                        Logger.Info($"[Protocol] skip_to_live received (monitor={monitorIndex}) - forcing keyframe for latency recovery");

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

                        if (_textureResizer != null && TextureResizer.NeedsResize(w, h))
                        {
                            // Get device for this monitor (each monitor has dedicated device)
                            var device = _capture?.GetDeviceForMonitor(monitorIndex);
                            if (device != null)
                            {
                                var (resized, rw, rh) = _textureResizer.ResizeBgraTexture(device, bgraTexture, w, h, monitorIndex);
                                textureToSend = resized;
                                targetWidth = rw;
                                targetHeight = rh;
                            }
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
                                await _ws.CloseAsync(
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

        #endregion

        #region Helper Methods

        private void SetPhase(ConnectionPhase phase)
        {
            _phase = phase;
            Logger.Info($"[Protocol] Phase changed to: {phase}");
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
                        Logger.Info($"[Protocol] Client codec capabilities: HEVC={_clientCodecCapability.SupportsHevc}, " +
                                          $"preferred={_clientCodecCapability.PreferredCodec}, device={_clientCodecCapability.DeviceModel}");

                        // Negotiate codec: Use H.265 if both server and client support it
                        NegotiateCodec();
                    }
                    else
                    {
                        Logger.Info("[Protocol] No client codec capabilities in hardware_info_ack, using H.264");
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

            Logger.Info($"[Protocol] Server supported codecs: [{string.Join(", ", serverCodecs)}]");

            // Get client supported codecs
            var clientCodecs = _clientCodecCapability?.SupportedCodecs ?? new[] { "H264" };
            Logger.Info($"[Protocol] Client supported codecs: [{string.Join(", ", clientCodecs)}]");

            // Find best mutual codec (priority: H264 first for hardware acceleration)
            string[] priority = { "H264", "H265", "VP9", "VP8" };

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
            Logger.Info("[Protocol] Handling restart_phase2 request...");

            try
            {
                // 1. Stop current streaming if active
                if (_streamer != null)
                {
                    Logger.Info("[Protocol] Stopping current stream for restart...");
                    _streamer.Dispose();
                    _streamer = null;
                }

                // 2. Stop capture if active
                if (_sharedCapture != null)
                {
                    Logger.Info("[Protocol] Stopping capture for restart...");
                    _sharedCapture.Stop();
                    _sharedCapture.Dispose();
                    _sharedCapture = null;
                }

                // 3. Get current monitor count from display config
                int actualMonitors = _displayConfig?.Monitors ?? _monitors.Count;
                actualMonitors = Math.Min(actualMonitors, _monitors.Count);

                // 4. Send config_complete to client to trigger new ICE negotiation
                Logger.Info("[Protocol] Sending config_complete for Phase 2 restart");
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

            // Stop keep-alive timer
            StopKeepAlive();

            // Stop capture
            try { _captureCts?.Cancel(); } catch { }
            try { _captureThread?.Join(500); } catch { }

            // Dispose streamer
            _streamer?.Dispose();
            _streamer = null;

            // Dispose texture resizer
            _textureResizer?.Dispose();
            _textureResizer = null;

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
                Logger.Info("[Protocol] Restoring display settings...");
                try
                {
                    DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(15));
                    Logger.Info("[Protocol] Display settings restored.");
                    _displayModified = false;

                    // Reset monitor count to force VDD setup on next session
                    // Without this, reconnecting with same monitor count would skip VDD setup
                    DisplayConfig.MonitorCount = 1;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Protocol] Restore failed: {ex.Message}");
                }
            }

            // Close WebSocket
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }

            Logger.Info($"[Protocol] Client {_clientId} disconnected");
        }

        #endregion

        #region Native Methods

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

        #endregion

        #region Cursor Tracking (DXGI Desktop Duplication)

        #region DXGI Cursor Conversion Helpers

        /// <summary>
        /// DXGI Pointer Shape Types
        /// </summary>
        private const int DXGI_POINTER_SHAPE_TYPE_MONOCHROME = 1;
        private const int DXGI_POINTER_SHAPE_TYPE_COLOR = 2;
        private const int DXGI_POINTER_SHAPE_TYPE_MASKED_COLOR = 4;

        /// <summary>
        /// Convert DXGI cursor buffer to RGBA32 format for Unity.
        /// Handles Monochrome, Color, and MaskedColor cursor types.
        /// </summary>
        private (byte[] rgbaData, int width, int height, int hotspotX, int hotspotY)? ConvertDxgiCursorToRgba(
            byte[] buffer, Vortice.DXGI.OutduplPointerShapeInfo shapeInfo)
        {
            int width = (int)shapeInfo.Width;
            int height = (int)shapeInfo.Height;
            int pitch = (int)shapeInfo.Pitch;
            int hotspotX = shapeInfo.HotSpot.X;
            int hotspotY = shapeInfo.HotSpot.Y;
            uint type = shapeInfo.Type;

            // Debug logging for cursor dimensions
            string typeName = type switch
            {
                DXGI_POINTER_SHAPE_TYPE_MONOCHROME => "MONOCHROME",
                DXGI_POINTER_SHAPE_TYPE_COLOR => "COLOR",
                DXGI_POINTER_SHAPE_TYPE_MASKED_COLOR => "MASKED_COLOR",
                _ => $"UNKNOWN({type})"
            };

            try
            {
                byte[] rgbaData;

                switch (type)
                {
                    case DXGI_POINTER_SHAPE_TYPE_MONOCHROME:
                        rgbaData = ConvertMonochromeCursorToRgba(buffer, width, height, pitch);
                        // Monochrome cursor height is doubled (AND mask + XOR mask)
                        height /= 2;
                        break;

                    case DXGI_POINTER_SHAPE_TYPE_COLOR:
                    case DXGI_POINTER_SHAPE_TYPE_MASKED_COLOR:
                        rgbaData = ConvertBgraCursorToRgba(buffer, width, height, pitch, type == DXGI_POINTER_SHAPE_TYPE_MASKED_COLOR);
                        break;

                    default:
                        return null;
                }

                return (rgbaData, width, height, hotspotX, hotspotY);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Convert Monochrome cursor (1bpp AND/XOR masks) to RGBA32.
        /// </summary>
        private byte[] ConvertMonochromeCursorToRgba(byte[] buffer, int width, int height, int pitch)
        {
            // Monochrome cursor has doubled height: AND mask on top, XOR mask on bottom
            int actualHeight = height / 2;
            byte[] rgbaData = new byte[width * actualHeight * 4];

            // Calculate bytes per row (1 bit per pixel, aligned to pitch)
            int bytesPerRow = pitch;

            // Validate buffer size: need enough for both AND and XOR masks
            int expectedBufferSize = bytesPerRow * height;
            if (buffer.Length < expectedBufferSize)
            {
                return rgbaData;
            }

            for (int y = 0; y < actualHeight; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int byteIndex = x / 8;
                    int bitIndex = 7 - (x % 8);

                    // AND mask (top half)
                    int andRowOffset = y * bytesPerRow;
                    int andOffset = andRowOffset + byteIndex;
                    if (andOffset >= buffer.Length) continue;
                    byte andByte = buffer[andOffset];
                    int andBit = (andByte >> bitIndex) & 1;

                    // XOR mask (bottom half)
                    int xorRowOffset = (actualHeight + y) * bytesPerRow;
                    int xorOffset = xorRowOffset + byteIndex;
                    if (xorOffset >= buffer.Length) continue;
                    byte xorByte = buffer[xorOffset];
                    int xorBit = (xorByte >> bitIndex) & 1;

                    // Calculate RGBA based on AND/XOR combination
                    // AND=0, XOR=0 -> Black, opaque
                    // AND=0, XOR=1 -> White, opaque
                    // AND=1, XOR=0 -> Transparent
                    // AND=1, XOR=1 -> Inverse (we'll render as gray to show it)
                    byte r, g, b, a;

                    if (andBit == 0)
                    {
                        // Opaque pixel
                        a = 255;
                        if (xorBit == 0)
                        {
                            r = g = b = 0; // Black
                        }
                        else
                        {
                            r = g = b = 255; // White
                        }
                    }
                    else
                    {
                        if (xorBit == 0)
                        {
                            // Transparent
                            r = g = b = a = 0;
                        }
                        else
                        {
                            // Inverse pixel - render as semi-transparent gray
                            r = g = b = 128;
                            a = 180;
                        }
                    }

                    // Output RGBA - NO FLIP, keep original Windows row order
                    // Each client (Unity/Web) will handle Y orientation if needed
                    int dstOffset = (y * width + x) * 4;
                    rgbaData[dstOffset] = r;
                    rgbaData[dstOffset + 1] = g;
                    rgbaData[dstOffset + 2] = b;
                    rgbaData[dstOffset + 3] = a;
                }
            }

            return rgbaData;
        }

        /// <summary>
        /// Convert BGRA cursor (32bpp) to RGBA32.
        /// Keeps original Windows row order (row 0 = top).
        /// Unity client will flip when loading into Texture2D if needed.
        /// </summary>
        private byte[] ConvertBgraCursorToRgba(byte[] buffer, int width, int height, int pitch, bool isMaskedColor)
        {
            byte[] rgbaData = new byte[width * height * 4];

            // Validate and auto-correct pitch if needed
            int expectedPitchMin = width * 4; // Minimum pitch for BGRA (32bpp)
            int expectedBufferSize = pitch * height;

            // Auto-detect pitch from buffer size if reported pitch seems wrong
            if (buffer.Length != expectedBufferSize && height > 0)
            {
                int detectedPitch = buffer.Length / height;
                if (detectedPitch >= expectedPitchMin && (buffer.Length % height) == 0)
                {
                    pitch = detectedPitch;
                    expectedBufferSize = pitch * height;
                }
            }

            if (buffer.Length < expectedBufferSize)
            {
                // Try with minimum pitch as fallback
                if (buffer.Length >= expectedPitchMin * height)
                {
                    pitch = expectedPitchMin;
                }
                else
                {
                    // Fill with transparent pixels
                    return rgbaData;
                }
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcOffset = y * pitch + x * 4;

                    // Bounds check
                    if (srcOffset + 3 >= buffer.Length)
                    {
                        continue;
                    }

                    // BGRA format
                    byte b = buffer[srcOffset];
                    byte g = buffer[srcOffset + 1];
                    byte r = buffer[srcOffset + 2];
                    byte a = buffer[srcOffset + 3];

                    // For masked color cursors, alpha has special meaning:
                    // 0x00 = use cursor color directly (OPAQUE, not transparent!)
                    // 0xFF = XOR with background (we render as semi-transparent)
                    if (isMaskedColor)
                    {
                        if (a == 0x00)
                        {
                            // Opaque pixel - use cursor color directly
                            a = 255;
                        }
                        else if (a == 0xFF)
                        {
                            // XOR pixel - render with reduced opacity to show it
                            a = 200;
                        }
                        // Other alpha values: keep as-is (shouldn't happen for MASKED_COLOR)
                    }

                    // Output RGBA - NO FLIP, keep original Windows row order (row 0 = top)
                    // Web clients can use directly, Unity clients flip when loading
                    int dstOffset = (y * width + x) * 4;
                    rgbaData[dstOffset] = r;
                    rgbaData[dstOffset + 1] = g;
                    rgbaData[dstOffset + 2] = b;
                    rgbaData[dstOffset + 3] = a;
                }
            }

            return rgbaData;
        }

        #endregion

        // DXGI Cursor state tracking
        private long _lastDxgiCursorShapeId = -1;
        private readonly object _dxgiCursorLock = new();
        private byte[]? _pendingDxgiCursorBuffer;
        private Vortice.DXGI.OutduplPointerShapeInfo? _pendingDxgiCursorShapeInfo;
        private Vortice.DXGI.OutduplPointerPosition? _pendingDxgiCursorPosition;
        private long _pendingDxgiCursorShapeId;
        private int _pendingDxgiCursorMonitorIndex = -1;

        /// <summary>
        /// Handle cursor update from PerMonitorCapture DXGI Desktop Duplication.
        /// Called from capture thread - stores data for processing in cursor tracking task.
        /// </summary>
        private void HandleDxgiCursorUpdate(int monitorIndex, byte[] buffer, Vortice.DXGI.OutduplPointerShapeInfo shapeInfo,
            Vortice.DXGI.OutduplPointerPosition position, long shapeId)
        {
            lock (_dxgiCursorLock)
            {
                _pendingDxgiCursorBuffer = buffer;
                _pendingDxgiCursorShapeInfo = shapeInfo;
                _pendingDxgiCursorPosition = position;
                _pendingDxgiCursorShapeId = shapeId;
                _pendingDxgiCursorMonitorIndex = monitorIndex;
            }
        }

        /// <summary>
        /// Start cursor tracking task that sends position and image updates to client.
        /// Uses DXGI Desktop Duplication cursor data from PerMonitorCapture instead of GDI+.
        /// </summary>
        private Task StartCursorTrackingTask()
        {
            _cursorCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            var ct = _cursorCts.Token;

            // Clear cache at start to ensure fresh cursors each session
            _sentCursorIds.Clear();
            _lastDxgiCursorShapeId = -1;

            return Task.Run(async () =>
            {
                const int POLL_INTERVAL_MS = 16; // ~60Hz
                const float THRESHOLD = 0.001f; // Minimum UV change to send update

                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    try
                    {
                        // Get cursor data from DXGI Desktop Duplication (thread-safe copy)
                        byte[]? cursorBuffer;
                        Vortice.DXGI.OutduplPointerShapeInfo? shapeInfo;
                        Vortice.DXGI.OutduplPointerPosition? position;
                        long shapeId;
                        int monitorIndex;

                        lock (_dxgiCursorLock)
                        {
                            cursorBuffer = _pendingDxgiCursorBuffer;
                            shapeInfo = _pendingDxgiCursorShapeInfo;
                            position = _pendingDxgiCursorPosition;
                            shapeId = _pendingDxgiCursorShapeId;
                            monitorIndex = _pendingDxgiCursorMonitorIndex;
                        }

                        // Wait for DXGI data before processing
                        if (cursorBuffer == null || !shapeInfo.HasValue || !position.HasValue || monitorIndex < 0)
                        {
                            await Task.Delay(POLL_INTERVAL_MS, ct);
                            continue;
                        }

                        // DXGI Visible flag is only true when cursor was updated in current frame
                        // When cursor is stationary, this flag is false - but cursor should still be shown
                        // So we always consider cursor visible (DXGI always has a cursor to show)
                        // The cursor is only truly hidden when app explicitly hides it (which we can't detect reliably)
                        bool visible = true; // Always visible - cursor overlay stays on

                        // Calculate UV coordinates from screen position
                        // DXGI cursor Position is RELATIVE to the monitor (not desktop coordinates!)
                        // Each output's Desktop Duplication reports cursor position relative to that output
                        // So we do NOT subtract monitor origin - just normalize to 0-1 range
                        float u = _lastCursorU, v = _lastCursorV;
                        if (monitorIndex < _monitorRects.Count)
                        {
                            var rect = _monitorRects[monitorIndex];
                            // Position is already relative to monitor - just normalize
                            u = (float)position.Value.Position.X / rect.w;
                            v = (float)position.Value.Position.Y / rect.h;
                            // Clamp to valid range
                            u = Math.Clamp(u, 0f, 1f);
                            v = Math.Clamp(v, 0f, 1f);
                        }

                        // Check if cursor shape changed (need to send new image)
                        bool shapeChanged = shapeId != _lastDxgiCursorShapeId;

                        // Check if position changed significantly
                        bool positionChanged = monitorIndex != _lastCursorMonitor ||
                                               visible != _lastCursorVisible ||
                                               (Math.Abs(u - _lastCursorU) > THRESHOLD || Math.Abs(v - _lastCursorV) > THRESHOLD);

                        // Send cursor image if shape changed and we haven't sent it yet
                        if (shapeChanged && visible && !_sentCursorIds.Contains(shapeId))
                        {
                            var converted = ConvertDxgiCursorToRgba(cursorBuffer, shapeInfo.Value);
                            if (converted.HasValue)
                            {
                                var (rgbaData, width, height, hotspotX, hotspotY) = converted.Value;

                                // CRITICAL: Force exact size to prevent client distortion
                                // Client expects exactly width*height*4 bytes of RGBA data
                                int expectedSize = width * height * 4;
                                if (rgbaData.Length != expectedSize)
                                {
                                    var fixedData = new byte[expectedSize];
                                    Array.Copy(rgbaData, fixedData, Math.Min(rgbaData.Length, expectedSize));
                                    rgbaData = fixedData;
                                }

                                var base64 = Convert.ToBase64String(rgbaData);

                                var imgMsg = new CursorImageMessage
                                {
                                    CursorId = shapeId,
                                    CursorTypeValue = (int)shapeInfo.Value.Type,
                                    Width = width,
                                    Height = height,
                                    HotspotX = hotspotX,
                                    HotspotY = hotspotY,
                                    ImageBase64 = base64
                                };
                                await SendMessageAsync(imgMsg);
                                _sentCursorIds.Add(shapeId);
                            }
                        }

                        if (positionChanged || shapeChanged)
                        {
                            _lastCursorMonitor = monitorIndex;
                            _lastCursorU = u;
                            _lastCursorV = v;
                            _lastCursorVisible = visible;
                            _lastDxgiCursorShapeId = shapeId;

                            var msg = new CursorPositionMessage
                            {
                                MonitorIndex = monitorIndex,
                                U = u,
                                V = v,
                                Visible = visible,
                                CursorTypeValue = (int)shapeInfo.Value.Type,
                                CursorId = shapeId
                            };
                            await SendMessageAsync(msg);
                        }

                        await Task.Delay(POLL_INTERVAL_MS, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception) { }
                }
            }, ct);
        }

        /// <summary>
        /// Stop cursor tracking task and clear sent cursor cache.
        /// </summary>
        private void StopCursorTracking()
        {
            try { _cursorCts?.Cancel(); } catch { }
            _sentCursorIds.Clear();
        }

        #endregion
    }
}
