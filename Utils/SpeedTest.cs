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
        public double BandwidthMbps { get; set; } // Download speed only (upload test removed)
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

                // Step 2: Bandwidth test (download only, upload removed)
                Console.WriteLine("[SpeedTest] Testing bandwidth...");
                result.BandwidthMbps = await TestDownloadAsync(ws, ct);

                // Classify connection type based on metrics
                result.ConnectionType = ClassifyConnection(result.PingMs, result.BandwidthMbps);
                result.Success = true;

                Console.WriteLine($"[SpeedTest] Complete: Ping={result.PingMs:F1}ms, Bandwidth={result.BandwidthMbps:F1}Mbps, Type={result.ConnectionType}");
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

            // Calculate recommended bitrate per monitor based on resolution and network
            double availableBandwidth = network.BandwidthMbps > 0 ? network.BandwidthMbps : 100;

            // Base bitrate recommendation based on resolution (realistic for H.264/H.265)
            // These are "high quality" targets - not maximum possible
            int baseBitrateKbps;
            if (config.ResolutionWidth >= 1920)
                baseBitrateKbps = 15000;  // 1080p: 15 Mbps is excellent quality
            else if (config.ResolutionWidth >= 1600)
                baseBitrateKbps = 12000;  // 900p: 12 Mbps
            else
                baseBitrateKbps = 10000;  // 768p: 10 Mbps

            // Scale up slightly if network is very good (low ping, high bandwidth)
            if (network.PingMs < 10 && availableBandwidth > 500)
                baseBitrateKbps = (int)(baseBitrateKbps * 1.3);  // +30% for excellent network
            else if (network.PingMs < 20 && availableBandwidth > 200)
                baseBitrateKbps = (int)(baseBitrateKbps * 1.15); // +15% for good network

            // Calculate max bitrate per monitor based on available bandwidth
            // Use 70% of bandwidth divided by 3 monitors as upper limit
            double maxBitratePerMonitor = availableBandwidth * 1000 * 0.7 / 3;

            // For desktop/text streaming, we need higher minimum bitrate to avoid artifacts
            // Text is harder to compress than video - use 15Mbps minimum for readability
            int minBitrateForText = 15000;

            // Use the lower of recommended and max available, clamped for text quality
            int rawBitrate = (int)Math.Clamp(Math.Min(baseBitrateKbps, maxBitratePerMonitor), minBitrateForText, 40000);

            // LAN quality override: maximize bitrate for local connections
            if (network.PingMs < 5 && availableBandwidth > 500)
            {
                rawBitrate = 40000;  // Max quality for LAN
            }

            // Round to nearest dropdown option: 5, 10, 15, 20, 30 Mbps
            config.BitrateKbps = RoundToNearestBitrateOption(rawBitrate);

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

        /// <summary>
        /// Round bitrate to nearest dropdown option: 15, 20, 25, 30, 40 Mbps
        /// Higher minimum for text/desktop streaming quality
        /// </summary>
        private static int RoundToNearestBitrateOption(int bitrateKbps)
        {
            // Options in Kbps - minimum 15Mbps for text clarity
            int[] options = { 15000, 20000, 25000, 30000, 40000 };

            int nearest = options[0];
            int minDiff = Math.Abs(bitrateKbps - options[0]);

            for (int i = 1; i < options.Length; i++)
            {
                int diff = Math.Abs(bitrateKbps - options[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    nearest = options[i];
                }
            }

            return nearest;
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
            reasons.Add($"{config.BitrateKbps / 1000}Mbps (BW: {network.BandwidthMbps:F0}Mbps)");

            // FPS reason
            reasons.Add($"{config.Fps}fps ({encoder.Type}, ping: {network.PingMs:F0}ms)");

            return string.Join(" | ", reasons);
        }
    }
}
