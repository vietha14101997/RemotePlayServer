#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network
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

                // Detect USB version based on interface speed and description
                result.InterfaceType = DetectUsbVersion(usbInfo);
                result.EstimatedBandwidthMbps = result.InterfaceType.Contains("3.") 
                    ? USB_3_BANDWIDTH_MBPS 
                    : USB_2_BANDWIDTH_MBPS;

                Logger.Info($"[UsbLatency] Measuring latency to gateway {usbInfo.GatewayIP} ({result.InterfaceType})...");

                // Measure ICMP ping latency
                var (avgLatency, jitter) = await MeasureIcmpLatencyAsync(usbInfo.GatewayIP);
                
                if (avgLatency > 0)
                {
                    result.LatencyMs = avgLatency;
                    result.JitterMs = jitter;  // Use actual measured jitter, not hardcoded
                    Logger.Info($"[UsbLatency] ✓ ICMP RTT: {avgLatency:F2}ms, Jitter: {jitter:F2}ms");
                }
                else
                {
                    // ICMP blocked - use defaults
                    result.LatencyMs = USB_DEFAULT_LATENCY_MS;
                    result.JitterMs = USB_DEFAULT_JITTER_MS;
                    result.Error = "ICMP ping blocked, using default values";
                    Logger.Info($"[UsbLatency] ⚠ ICMP blocked, using defaults: {USB_DEFAULT_LATENCY_MS}ms");
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Logger.Error($"[UsbLatency] ✗ Error: {ex.Message}");
                
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
        /// Detect USB version using multiple methods:
        /// 1. WMI query for USB controllers (most reliable)
        /// 2. Registry check for USB host controllers
        /// 3. Network interface speed (fallback, unreliable for RNDIS)
        /// 
        /// Note: RNDIS reports its own virtual link speed (~426 Mbps) regardless
        /// of actual USB port speed, so we must use other methods.
        /// </summary>
        private static string DetectUsbVersion(UsbTetheringHelper.UsbTetheringInfo usbInfo)
        {
            // Method 1: Check WMI for USB 3.0 controllers with connected devices
            try
            {
                var usbVersion = DetectUsbVersionViaWmi();
                if (!string.IsNullOrEmpty(usbVersion))
                {
                    Logger.Info($"[UsbLatency] USB version detected via WMI: {usbVersion}");
                    return usbVersion;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[UsbLatency] WMI USB detection failed: {ex.Message}");
            }

            // Method 2: Check Registry for xHCI (USB 3.0) host controllers
            try
            {
                var usbVersion = DetectUsbVersionViaRegistry();
                if (!string.IsNullOrEmpty(usbVersion))
                {
                    Logger.Info($"[UsbLatency] USB version detected via Registry: {usbVersion}");
                    return usbVersion;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[UsbLatency] Registry USB detection failed: {ex.Message}");
            }

            // Method 3: Check interface description for clues
            string description = usbInfo.Description ?? "";
            if (description.Contains("USB 3", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("USB3", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("SuperSpeed", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("xHCI", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"[UsbLatency] USB 3.0 detected from interface description");
                return "USB 3.0";
            }

            // Method 4: Fallback to link speed (unreliable for RNDIS but try anyway)
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name == usbInfo.InterfaceName);

                if (ni != null)
                {
                    double speedMbps = ni.Speed / 1_000_000.0;
                    Logger.Info($"[UsbLatency] Interface '{usbInfo.InterfaceName}' link speed: {speedMbps:F0} Mbps (RNDIS virtual speed)");
                    
                    // Note: RNDIS typically reports ~426 Mbps regardless of USB version
                    // This is NOT a reliable indicator
                    if (speedMbps > 1000) // Very high speed might indicate USB 3.0
                    {
                        return "USB 3.0";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[UsbLatency] Interface speed check failed: {ex.Message}");
            }

            Logger.Info($"[UsbLatency] USB version defaulting to USB 2.0");
            return "USB 2.0";
        }

        /// <summary>
        /// Use WMI to detect USB 3.0 host controllers with connected devices.
        /// This is the most reliable method on Windows.
        /// </summary>
        private static string? DetectUsbVersionViaWmi()
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_USBController");

            bool hasUsb30Controller = false;
            bool hasUsb20Controller = false;

            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                string desc = obj["Description"]?.ToString() ?? "";
                string pnpClass = obj["PNPClass"]?.ToString() ?? "";
                
                // Check for USB 3.0/3.1/3.2 indicators
                if (name.Contains("xHCI", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("USB 3", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("USB3", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("xHCI", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("USB 3", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("eXtensible Host Controller", StringComparison.OrdinalIgnoreCase))
                {
                    hasUsb30Controller = true;
                    Logger.Info($"[UsbLatency] Found USB 3.x controller: {name}");
                }
                else if (name.Contains("EHCI", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("USB 2", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("Enhanced Host Controller", StringComparison.OrdinalIgnoreCase))
                {
                    hasUsb20Controller = true;
                }
            }

            // Also check for USB hub to see which controller the device is connected to
            using var hubSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_USBHub WHERE Status='OK'");

            foreach (var obj in hubSearcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                string deviceId = obj["DeviceID"]?.ToString() ?? "";
                
                // USB 3.0 devices often have SuperSpeed in their name or USB\\VID_xxxx&PID_xxxx\\x pattern
                if (name.Contains("SuperSpeed", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("USB 3", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info($"[UsbLatency] Found USB 3.0 hub: {name}");
                    hasUsb30Controller = true;
                }
            }

            if (hasUsb30Controller)
            {
                return "USB 3.0";
            }
            else if (hasUsb20Controller)
            {
                return "USB 2.0";
            }

            return null;
        }

        /// <summary>
        /// Check Windows Registry for USB host controller types.
        /// </summary>
        private static string? DetectUsbVersionViaRegistry()
        {
            try
            {
                // Look for xHCI controllers (USB 3.0+)
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\USBXHCI");
                
                if (key != null)
                {
                    Logger.Info("[UsbLatency] USB xHCI service found (USB 3.x support present)");
                    return "USB 3.0";
                }
            }
            catch { }

            try
            {
                // Alternative: Check Enum\USB for connected devices
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\USB");
                
                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        // VID_xxxx&PID_xxxx pattern
                        if (subKeyName.Contains("VID_", StringComparison.OrdinalIgnoreCase))
                        {
                            using var subKey = key.OpenSubKey(subKeyName);
                            if (subKey != null)
                            {
                                foreach (var instanceName in subKey.GetSubKeyNames())
                                {
                                    using var instanceKey = subKey.OpenSubKey(instanceName);
                                    var friendlyName = instanceKey?.GetValue("FriendlyName")?.ToString() ?? "";
                                    
                                    if (friendlyName.Contains("USB 3", StringComparison.OrdinalIgnoreCase) ||
                                        friendlyName.Contains("SuperSpeed", StringComparison.OrdinalIgnoreCase))
                                    {
                                        return "USB 3.0";
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
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
