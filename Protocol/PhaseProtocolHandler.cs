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
                await SendErrorAsync(GetPhaseNumber(), "UNEXPECTED_ERROR", ex.Message);
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

            // Wait for client to acknowledge hardware info before speed test
            Console.WriteLine("[Protocol] Waiting for hardware_info_ack...");
            await WaitForHardwareAckAsync();
            Console.WriteLine("[Protocol] Received hardware_info_ack, starting speed test");

            // Run speed test
            SetPhase(ConnectionPhase.Phase1_SpeedTest);
            Console.WriteLine("[Protocol] Phase 1: Running speed test...");
            _speedTestResult = await SpeedTest.RunFullTestAsync(_ws, _ct);

            // Send network info
            // Use actual adapter type from HardwareInfo (Ethernet/WiFi) instead of metrics-based classification
            var netMsg = new NetworkInfoMessage
            {
                PingMs = _speedTestResult.PingMs,
                JitterMs = _speedTestResult.JitterMs,
                BandwidthMbps = Math.Max(_speedTestResult.DownloadMbps, _speedTestResult.UploadMbps),
                ConnectionType = _hardwareInfo.Network.ConnectionType
            };
            await SendMessageAsync(netMsg);
            Console.WriteLine($"[Protocol] Sent network info: {netMsg.PingMs:F1}ms ping, {netMsg.BandwidthMbps:F1}Mbps");

            // Calculate and send suggested config
            var suggested = StreamingOptimizer.CalculateSuggestedConfig(_hardwareInfo, _encoderInfo, _speedTestResult);
            var sugMsg = new SuggestedConfigMessage
            {
                Monitors = suggested.Monitors,
                Resolution = new ResolutionDto { Width = suggested.ResolutionWidth, Height = suggested.ResolutionHeight },
                BitrateKbps = suggested.BitrateKbps,
                Fps = suggested.Fps,
                RefreshRate = suggested.RefreshRate,
                Reason = suggested.Reason
            };
            await SendMessageAsync(sugMsg);
            Console.WriteLine($"[Protocol] Sent suggested config: {suggested.Monitors}x{suggested.ResolutionWidth}x{suggested.ResolutionHeight}@{suggested.Fps}fps");

            // Wait for proceed
            SetPhase(ConnectionPhase.Phase1_WaitingProceed);
            Console.WriteLine("[Protocol] Phase 1: Waiting for client proceed...");
            await WaitForProceedAsync(2);
            Console.WriteLine("[Protocol] Phase 1 complete, proceeding to Phase 2");
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

            // Create MultiPCStreamer
            _streamer = new RemotePlayServer.Encoding.MultiPCStreamer(
                actualMonitors, config.Fps, config.BitrateKbps, _capture.Device);

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
            try
            {
                var answerSdp = await _streamer.ProcessOfferAsync(
                    monitorIndex, offerSdp,
                    _displayConfig?.Resolution.Width ?? 1920,
                    _displayConfig?.Resolution.Height ?? 1080);

                // Send answer (JSON format)
                var answerMsg = new AnswerMessage { MonitorIndex = monitorIndex, Sdp = answerSdp };
                var answerJson = ProtocolMessageParser.Serialize(answerMsg);
                Console.WriteLine($"[Protocol] Answer JSON for m{monitorIndex}: {answerJson.Substring(0, Math.Min(150, answerJson.Length))}...");
                await SendTextAsync(answerJson);
                Console.WriteLine($"[Protocol] Sent answer for monitor {monitorIndex}, len={answerJson.Length} bytes");

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

                    var msgType = ProtocolMessageParser.GetMessageType(text);
                    if (msgType == "stop_streaming" || text.Equals("stop_streaming", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[Protocol] Received stop_streaming");
                        break;
                    }

                    // Handle late ICE candidates
                    if (msgType == "candidate" || text.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleLegacyMessageAsync(text);
                    }
                }
                catch (OperationCanceledException) { break; }
            }

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
                        await Task.Delay(500, _captureCts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            });
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

                // Handle ping
                if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await SendTextAsync("pong");
                    continue;
                }

                // Check for hardware_info_ack
                var msgType = ProtocolMessageParser.GetMessageType(text);
                if (msgType == "hardware_info_ack")
                {
                    return;
                }
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

        #endregion
    }
}
