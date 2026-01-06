#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// USB Tethering (RNDIS) network latency measurement result.
    /// </summary>
    public class UsbNetworkLatencyResult
    {
        /// <summary>True if USB tethering is active and measurements are from USB interface.</summary>
        public bool IsUsbMode { get; set; }

        /// <summary>ICMP ping latency in milliseconds (typically < 1ms for USB).</summary>
        public double LatencyMs { get; set; }

        /// <summary>Jitter in milliseconds (typically near 0 for USB).</summary>
        public double JitterMs { get; set; }

        /// <summary>Estimated bandwidth based on USB version (480 Mbps for USB 2.0, 5000 Mbps for USB 3.0).</summary>
        public double EstimatedBandwidthMbps { get; set; }

        /// <summary>USB interface version: "USB 2.0", "USB 3.0", or "Unknown".</summary>
        public string InterfaceType { get; set; } = "Unknown";

        /// <summary>Gateway IP address of the USB interface.</summary>
        public string? GatewayIP { get; set; }

        /// <summary>Error message if measurement failed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Measures network latency specifically for USB Tethering (RNDIS) connections.
    /// 
    /// USB Tethering characteristics:
    /// - Very low latency (< 1ms typical)
    /// - Minimal jitter
    /// - Stable bandwidth (USB 2.0: 480 Mbps, USB 3.0: 5 Gbps)
    /// - No packet loss under normal conditions
    /// 
    /// This class uses ICMP ping directly to the USB gateway (phone) instead of
    /// WebSocket-based ping, providing more accurate network-level latency.
    /// </summary>
    public static class UsbNetworkLatency
    {
        // Default values for USB mode when measurement is unavailable
        private const double USB_DEFAULT_LATENCY_MS = 0.5;
        private const double USB_DEFAULT_JITTER_MS = 0.1;
        private const double USB_2_BANDWIDTH_MBPS = 480.0;
        private const double USB_3_BANDWIDTH_MBPS = 5000.0;

        // Measurement configuration
        private const int PING_SAMPLES = 10;
        private const int PING_TIMEOUT_MS = 1000;
        private const int WARMUP_PINGS = 2;

        /// <summary>
        /// Measure USB network latency using ICMP ping to the gateway.
        /// </summary>
        /// <returns>Latency result with USB-specific metrics.</returns>
        public static async Task<UsbNetworkLatencyResult> MeasureAsync()
        {
            var result = new UsbNetworkLatencyResult { IsUsbMode = false };

            try
            {
                // Detect USB tethering interface
                var usbInfo = UsbTetheringHelper.Detect();
                if (!usbInfo.IsAvailable || string.IsNullOrEmpty(usbInfo.GatewayIP))
                {
                    result.Error = "USB tethering not detected or no gateway available";
                    return result;
                }

                result.IsUsbMode = true;
                result.GatewayIP = usbInfo.GatewayIP;

                // Detect USB version based on interface description
                result.InterfaceType = DetectUsbVersion(usbInfo.Description ?? "");
                result.EstimatedBandwidthMbps = result.InterfaceType.Contains("3.") 
                    ? USB_3_BANDWIDTH_MBPS 
                    : USB_2_BANDWIDTH_MBPS;

                Console.WriteLine($"[UsbLatency] Measuring latency to gateway {usbInfo.GatewayIP} ({result.InterfaceType})...");

                // Measure ICMP ping latency
                var (avgLatency, jitter) = await MeasureIcmpLatencyAsync(usbInfo.GatewayIP);
                
                if (avgLatency > 0)
                {
                    result.LatencyMs = avgLatency;
                    result.JitterMs = jitter;
                    Console.WriteLine($"[UsbLatency] ✓ ICMP RTT: {avgLatency:F2}ms, Jitter: {jitter:F2}ms");
                }
                else
                {
                    // ICMP blocked - use defaults
                    result.LatencyMs = USB_DEFAULT_LATENCY_MS;
                    result.JitterMs = USB_DEFAULT_JITTER_MS;
                    result.Error = "ICMP ping blocked, using default values";
                    Console.WriteLine($"[UsbLatency] ⚠ ICMP blocked, using defaults: {USB_DEFAULT_LATENCY_MS}ms");
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Console.WriteLine($"[UsbLatency] ✗ Error: {ex.Message}");
                
                // Return defaults for USB mode even on error
                if (result.IsUsbMode)
                {
                    result.LatencyMs = USB_DEFAULT_LATENCY_MS;
                    result.JitterMs = USB_DEFAULT_JITTER_MS;
                }
            }

            return result;
        }

        /// <summary>
        /// Get default USB latency result without measurement.
        /// Use when USB mode is detected but measurement is not needed or fails.
        /// </summary>
        public static UsbNetworkLatencyResult GetDefaultResult()
        {
            var usbInfo = UsbTetheringHelper.Detect();
            
            return new UsbNetworkLatencyResult
            {
                IsUsbMode = usbInfo.IsAvailable,
                LatencyMs = USB_DEFAULT_LATENCY_MS,
                JitterMs = USB_DEFAULT_JITTER_MS,
                EstimatedBandwidthMbps = USB_2_BANDWIDTH_MBPS,
                InterfaceType = "USB 2.0 (default)",
                GatewayIP = usbInfo.GatewayIP
            };
        }

        /// <summary>
        /// Measure ICMP ping latency to a target IP.
        /// </summary>
        private static async Task<(double avgMs, double jitterMs)> MeasureIcmpLatencyAsync(string targetIp)
        {
            var times = new System.Collections.Generic.List<double>();

            using var ping = new Ping();

            // Warmup pings (discard)
            for (int i = 0; i < WARMUP_PINGS; i++)
            {
                try
                {
                    await ping.SendPingAsync(targetIp, PING_TIMEOUT_MS);
                    await Task.Delay(10);
                }
                catch { }
            }

            // Measurement pings
            for (int i = 0; i < PING_SAMPLES; i++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(targetIp, PING_TIMEOUT_MS);
                    sw.Stop();

                    if (reply.Status == IPStatus.Success)
                    {
                        // Use our own timing for sub-ms accuracy
                        // (PingReply.RoundtripTime is in ms and rounds to integer)
                        double rtt = sw.Elapsed.TotalMilliseconds;
                        times.Add(rtt);
                    }

                    // Small delay between samples
                    if (i < PING_SAMPLES - 1)
                        await Task.Delay(20);
                }
                catch (PingException)
                {
                    // ICMP might be blocked
                    continue;
                }
            }

            if (times.Count == 0)
                return (0, 0);

            // Calculate statistics, removing outliers
            times.Sort();
            
            // Remove top 10% outliers for more stable measurement
            int trimCount = Math.Max(1, times.Count / 10);
            var trimmedTimes = times.Take(times.Count - trimCount).ToList();

            if (trimmedTimes.Count == 0)
                trimmedTimes = times;

            double avg = trimmedTimes.Average();

            // Calculate jitter (average difference between consecutive samples)
            double jitter = 0;
            if (times.Count > 1)
            {
                jitter = times.Skip(1)
                    .Select((t, i) => Math.Abs(t - times[i]))
                    .Average();
            }

            return (avg, jitter);
        }

        /// <summary>
        /// Detect USB version from interface description.
        /// </summary>
        private static string DetectUsbVersion(string description)
        {
            // Common USB 3.0 indicators
            if (description.Contains("USB 3", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("USB3", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("SuperSpeed", StringComparison.OrdinalIgnoreCase))
            {
                return "USB 3.0";
            }

            // Most RNDIS connections are USB 2.0
            return "USB 2.0";
        }

        /// <summary>
        /// Synchronous version for use in non-async contexts.
        /// </summary>
        public static UsbNetworkLatencyResult Measure()
        {
            return MeasureAsync().GetAwaiter().GetResult();
        }
    }
}
