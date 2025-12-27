#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// Result of speed test including ping and bandwidth measurements.
    /// </summary>
    public class SpeedTestResult
    {
        public bool Success { get; set; }
        public double PingMs { get; set; }
        public double JitterMs { get; set; }
        public double DownloadMbps { get; set; }
        public double UploadMbps { get; set; }
        public string ConnectionType { get; set; } = "Unknown"; // LAN, WiFi, Internet
        public string? Error { get; set; }
    }

    /// <summary>
    /// Speed test protocol for measuring actual bandwidth over WebSocket.
    /// </summary>
    public static class SpeedTest
    {
        // Test configuration
        private const int ChunkSizeBytes = 64 * 1024;  // 64KB per chunk
        private const int DefaultChunks = 16;          // 1MB total by default
        private const int MaxChunks = 128;             // 8MB max
        private const int PingSamples = 5;             // Number of ping samples
        private const int TestDurationMs = 2000;       // 2 seconds per direction

        /// <summary>
        /// Run complete speed test (ping + download + upload).
        /// </summary>
        public static async Task<SpeedTestResult> RunFullTestAsync(
            WebSocket ws,
            CancellationToken ct = default)
        {
            var result = new SpeedTestResult();

            try
            {
                // Step 1: Measure ping/RTT
                Console.WriteLine("[SpeedTest] Measuring ping...");
                var pingResult = await MeasurePingAsync(ws, ct);
                result.PingMs = pingResult.avgMs;
                result.JitterMs = pingResult.jitterMs;

                // Step 2: Download test (Server -> Client)
                Console.WriteLine("[SpeedTest] Testing download speed...");
                result.DownloadMbps = await TestDownloadAsync(ws, ct);

                // Step 3: Upload test (Client -> Server)
                Console.WriteLine("[SpeedTest] Testing upload speed...");
                result.UploadMbps = await TestUploadAsync(ws, ct);

                // Classify connection type based on metrics
                result.ConnectionType = ClassifyConnection(result.PingMs, result.DownloadMbps);
                result.Success = true;

                Console.WriteLine($"[SpeedTest] Complete: Ping={result.PingMs:F1}ms, Down={result.DownloadMbps:F1}Mbps, Up={result.UploadMbps:F1}Mbps, Type={result.ConnectionType}");
            }
            catch (OperationCanceledException)
            {
                result.Error = "Speed test cancelled";
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Console.WriteLine($"[SpeedTest] Error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Measure ping RTT using ping/pong messages.
        /// </summary>
        public static async Task<(double avgMs, double jitterMs)> MeasurePingAsync(
            WebSocket ws,
            CancellationToken ct = default)
        {
            var times = new List<double>();
            var buffer = new byte[64];

            for (int i = 0; i < PingSamples; i++)
            {
                var sw = Stopwatch.StartNew();

                // Send ping
                await SendTextAsync(ws, "ping", ct);

                // Wait for pong
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(5000);

                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    sw.Stop();

                    var response = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (response.Equals("pong", StringComparison.OrdinalIgnoreCase))
                    {
                        times.Add(sw.Elapsed.TotalMilliseconds);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Timeout, skip this sample
                }

                // Small delay between samples
                if (i < PingSamples - 1)
                    await Task.Delay(100, ct);
            }

            if (times.Count == 0)
                return (0, 0);

            double avg = times.Average();
            double jitter = times.Count > 1
                ? times.Skip(1).Select((t, i) => Math.Abs(t - times[i])).Average()
                : 0;

            return (avg, jitter);
        }

        /// <summary>
        /// Test download speed (Server -> Client).
        /// Sends binary chunks and waits for client acknowledgment.
        /// </summary>
        private static async Task<double> TestDownloadAsync(WebSocket ws, CancellationToken ct)
        {
            // Notify client that download test is starting
            var startMsg = JsonSerializer.Serialize(new
            {
                type = "speedtest_start",
                direction = "download",
                chunkSize = ChunkSizeBytes,
                durationMs = TestDurationMs
            });
            await SendTextAsync(ws, startMsg, ct);

            // Generate random chunk data
            var chunk = new byte[ChunkSizeBytes];
            new Random().NextBytes(chunk);

            var sw = Stopwatch.StartNew();
            long bytesSent = 0;

            // Send chunks for TestDurationMs
            while (sw.ElapsedMilliseconds < TestDurationMs)
            {
                await ws.SendAsync(
                    new ArraySegment<byte>(chunk),
                    WebSocketMessageType.Binary,
                    true,
                    ct);
                bytesSent += chunk.Length;
            }

            // Send end marker
            var endMsg = JsonSerializer.Serialize(new
            {
                type = "speedtest_end",
                direction = "download",
                totalBytes = bytesSent,
                durationMs = sw.ElapsedMilliseconds
            });
            await SendTextAsync(ws, endMsg, ct);

            // Wait for client acknowledgment with their measured speed
            var buffer = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(10000);

            try
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                var response = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);

                // Try to parse client's measured speed
                if (response.StartsWith("{"))
                {
                    var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("clientMbps", out var mbpsProp))
                    {
                        return mbpsProp.GetDouble();
                    }
                }
            }
            catch { }

            // Fallback: calculate server-side estimate
            double seconds = sw.Elapsed.TotalSeconds;
            return (bytesSent * 8.0) / (seconds * 1_000_000);
        }

        /// <summary>
        /// Test upload speed (Client -> Server).
        /// Requests client to send data and measures receive rate.
        /// </summary>
        private static async Task<double> TestUploadAsync(WebSocket ws, CancellationToken ct)
        {
            // Request client to start upload test
            var startMsg = JsonSerializer.Serialize(new
            {
                type = "speedtest_start",
                direction = "upload",
                chunkSize = ChunkSizeBytes,
                durationMs = TestDurationMs
            });
            await SendTextAsync(ws, startMsg, ct);

            var buffer = new byte[ChunkSizeBytes + 1024]; // Extra space for headers
            var sw = Stopwatch.StartNew();
            long bytesReceived = 0;
            bool testComplete = false;

            // Receive data until end marker or timeout
            while (!testComplete && sw.ElapsedMilliseconds < TestDurationMs + 5000)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);

                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        bytesReceived += result.Count;
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (text.Contains("speedtest_end"))
                        {
                            testComplete = true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            double seconds = sw.Elapsed.TotalSeconds;
            double mbps = (bytesReceived * 8.0) / (seconds * 1_000_000);

            // Send speedtest_end for upload (same format as download) so client can transition state
            var endMsg = JsonSerializer.Serialize(new
            {
                type = "speedtest_end",
                direction = "upload",
                totalBytes = bytesReceived,
                durationMs = sw.ElapsedMilliseconds,
                serverMbps = mbps
            });
            await SendTextAsync(ws, endMsg, ct);

            return mbps;
        }

        /// <summary>
        /// Classify connection type based on RTT and bandwidth.
        /// </summary>
        private static string ClassifyConnection(double pingMs, double mbps)
        {
            if (pingMs < 5 && mbps > 500)
                return "LAN";
            if (pingMs < 20 && mbps > 100)
                return "WiFi";
            return "Internet";
        }

        private static async Task SendTextAsync(WebSocket ws, string text, CancellationToken ct)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                ct);
        }
    }

    /// <summary>
    /// Helper class for generating suggested streaming configuration.
    /// </summary>
    public static class StreamingOptimizer
    {
        /// <summary>
        /// Calculate optimal streaming configuration based on hardware and network.
        /// </summary>
        public static SuggestedConfig CalculateSuggestedConfig(
            HardwareInfo hw,
            EncoderInfo encoder,
            SpeedTestResult network)
        {
            var config = new SuggestedConfig();

            // Resolution based on GPU VRAM
            if (hw.Gpu.VramMB >= 8192) // 8GB+
            {
                config.ResolutionWidth = 1920;
                config.ResolutionHeight = 1080;
            }
            else if (hw.Gpu.VramMB >= 4096) // 4GB
            {
                config.ResolutionWidth = 1600;
                config.ResolutionHeight = 900;
            }
            else // <4GB
            {
                config.ResolutionWidth = 1366;
                config.ResolutionHeight = 768;
            }

            // Calculate available bandwidth per monitor (70% of measured, divided by 3)
            double availableBandwidth = network.DownloadMbps > 0 ? network.DownloadMbps : 100;
            double bitratePerMonitor = availableBandwidth * 1000 * 0.7 / 3;

            // Clamp bitrate to reasonable range
            config.BitrateKbps = (int)Math.Clamp(bitratePerMonitor, 5000, 30000);

            // FPS based on encoder capability and ping
            if (encoder.HwAccel && network.PingMs < 20)
            {
                config.Fps = 60;
                config.RefreshRate = 60;
            }
            else if (encoder.HwAccel && network.PingMs < 50)
            {
                config.Fps = 45;
                config.RefreshRate = 60;
            }
            else
            {
                config.Fps = 30;
                config.RefreshRate = 60;
            }

            // Monitor count based on total available bandwidth
            double totalRequired = config.BitrateKbps * 3;
            double totalAvailable = availableBandwidth * 1000 * 0.7;

            if (totalAvailable >= totalRequired)
                config.Monitors = 3;
            else if (totalAvailable >= totalRequired * 2 / 3)
                config.Monitors = 2;
            else
                config.Monitors = 1;

            // Build reason string
            config.Reason = BuildReasonString(hw, encoder, network, config);

            return config;
        }

        private static string BuildReasonString(
            HardwareInfo hw,
            EncoderInfo encoder,
            SpeedTestResult network,
            SuggestedConfig config)
        {
            var reasons = new List<string>();

            // Resolution reason
            if (hw.Gpu.VramMB >= 8192)
                reasons.Add($"1080p (VRAM: {hw.Gpu.VramGB}GB)");
            else if (hw.Gpu.VramMB >= 4096)
                reasons.Add($"900p (VRAM: {hw.Gpu.VramGB}GB)");
            else
                reasons.Add($"768p (VRAM limited)");

            // Bitrate reason
            reasons.Add($"{config.BitrateKbps / 1000}Mbps (BW: {network.DownloadMbps:F0}Mbps)");

            // FPS reason
            reasons.Add($"{config.Fps}fps ({encoder.Type}, ping: {network.PingMs:F0}ms)");

            return string.Join(" | ", reasons);
        }
    }
}
