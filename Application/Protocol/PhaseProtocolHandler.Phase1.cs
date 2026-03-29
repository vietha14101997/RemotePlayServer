#nullable enable
using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Configuration;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Application.Protocol
{
    public partial class PhaseProtocolHandler
    {
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

            // Set GPU-aware max quality height
            var gpuTier = GpuTierClassifier.Classify(_encoderInfo.Type, _hardwareInfo.Gpu.VramMB);
            hwMsg.MaxQualityHeight = GpuTierClassifier.GetMaxQualityHeight(gpuTier);
            Logger.Info($"[Protocol] GPU tier: {gpuTier}, maxQualityHeight: {hwMsg.MaxQualityHeight}p");

            await SendMessageAsync(hwMsg);
            Logger.Info("[Protocol] Sent hardware info to client");

            // Wait for client to acknowledge hardware info
            Logger.Info("[Protocol] Waiting for hardware_info_ack...");
            await WaitForHardwareAckAsync();
            Logger.Info("[Protocol] Received hardware_info_ack");

            // Speed test removed — use sensible defaults based on transport type
            Logger.Info("[Protocol] Skipping speed test, using defaults");
            _speedTestResult = new SpeedTestResult
            {
                BandwidthMbps = _isUsbTransport ? 500.0 : 50.0,
                PingMs = _isUsbTransport ? 1.0 : 30.0,
                JitterMs = _isUsbTransport ? 0.5 : 5.0,
                ConnectionType = _isUsbTransport ? "USB" : (_isRelayTransport ? "Internet" : "LAN")
            };

            // Calculate and send suggested config based on Client's speed test results
            Logger.Info("[Protocol] Calculating suggested config...");
            var suggested = StreamingOptimizer.CalculateSuggestedConfig(_hardwareInfo, _encoderInfo, _speedTestResult);

            // Override RefreshRate with max supported monitor Hz (for client FPS option generation)
            // Uses max across all monitors' supported modes, not just current Hz.
            // If user selects FPS > current Hz, server will auto-switch monitor to that Hz.
            int maxMonitorHz = 60;
            foreach (var mon in _monitors)
            {
                int monMaxHz = DisplayUtil.GetMaxRefreshRate(mon.name);
                if (monMaxHz > maxMonitorHz) maxMonitorHz = monMaxHz;
            }
            suggested.RefreshRate = maxMonitorHz;
            Logger.Info($"[Protocol] Max supported monitor refresh rate: {maxMonitorHz}Hz");

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

            // Determine max native resolution height across all physical monitors
            int maxNativeHeight = _monitors.Count > 0
                ? _monitors.Max(m => m.height)
                : 1080;

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
                NetworkInfo = networkInfo,
                MaxNativeHeight = maxNativeHeight
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
                // Only count bytes — no need to store the payload
                if (result.MessageType == WebSocketMessageType.Binary)
                {
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
    }
}
