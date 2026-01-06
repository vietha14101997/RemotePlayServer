#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// Helper for detecting USB Tethering connection.
    /// USB Tethering creates a network interface that allows full TCP+UDP communication.
    ///
    /// When USB Tethering is enabled:
    /// - Android device creates a USB network interface (RNDIS)
    /// - PC gets an IP in the 192.168.42.x range (Android default) or similar
    /// - Both TCP (signaling) and UDP (WebRTC media) can use this interface
    /// </summary>
    public static class UsbTetheringHelper
    {
        /// <summary>
        /// Known USB tethering IP ranges.
        /// Android default: 192.168.42.x
        /// Some devices: 192.168.44.x, 192.168.137.x
        /// </summary>
        private static readonly string[] USB_TETHERING_PREFIXES = new[]
        {
            "192.168.42.",   // Android default USB tethering
            "192.168.44.",   // Some Samsung devices
            "192.168.137.",  // Windows ICS (Internet Connection Sharing)
        };

        /// <summary>
        /// Known USB tethering interface name patterns.
        /// </summary>
        private static readonly string[] USB_INTERFACE_PATTERNS = new[]
        {
            "RNDIS",                    // Remote NDIS (Android USB tethering)
            "USB Ethernet",             // Generic USB Ethernet
            "Remote NDIS",              // Full name
            "Android",                  // Some Android devices
            "USB Network",              // Generic
        };

        /// <summary>
        /// Result of USB tethering detection.
        /// </summary>
        public class UsbTetheringInfo
        {
            public bool IsAvailable { get; set; }
            public string? InterfaceName { get; set; }
            public string? ServerIP { get; set; }      // PC's IP on USB interface
            public string? GatewayIP { get; set; }     // Phone's IP (gateway)
            public string? Description { get; set; }
        }

        /// <summary>
        /// Detect if USB tethering is available and get connection info.
        /// </summary>
        public static UsbTetheringInfo Detect()
        {
            var result = new UsbTetheringInfo { IsAvailable = false };

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                    .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();

                foreach (var ni in interfaces)
                {
                    var ipProps = ni.GetIPProperties();
                    var ipv4Addresses = ipProps.UnicastAddresses
                        .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(ua => ua.Address.ToString())
                        .ToList();

                    // Check 1: IP is in USB tethering range
                    var usbIP = ipv4Addresses.FirstOrDefault(ip =>
                        USB_TETHERING_PREFIXES.Any(prefix => ip.StartsWith(prefix)));

                    // Check 2: Interface description contains "NDIS" (Remote NDIS is THE standard USB Tethering driver)
                    // This is a definitive indicator - RNDIS/Remote NDIS is specifically for USB networking
                    bool isNdisInterface = ni.Description.Contains("NDIS", StringComparison.OrdinalIgnoreCase);

                    // Detect if: IP in known range OR interface is clearly NDIS-based USB Tethering
                    if (usbIP != null || (isNdisInterface && ipv4Addresses.Count > 0))
                    {
                        var serverIP = usbIP ?? ipv4Addresses.First();
                        Console.WriteLine($"[USB-Tether] Detected by: {(usbIP != null ? "IP range" : "NDIS interface")}");

                        // Get gateway (phone's IP)
                        var gateway = ipProps.GatewayAddresses
                            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                        result.IsAvailable = true;
                        result.InterfaceName = ni.Name;
                        result.ServerIP = serverIP;
                        result.GatewayIP = gateway?.Address.ToString();
                        result.Description = ni.Description;

                        Console.WriteLine($"[USB-Tether] Found interface: {ni.Name}");
                        Console.WriteLine($"[USB-Tether] Description: {ni.Description}");
                        Console.WriteLine($"[USB-Tether] Server IP: {serverIP}");
                        Console.WriteLine($"[USB-Tether] Gateway (phone): {gateway?.Address}");

                        return result;
                    }
                }

                // Fallback: Check for any interface with USB tethering IP range
                foreach (var ni in interfaces)
                {
                    var ipProps = ni.GetIPProperties();
                    var ipv4Addresses = ipProps.UnicastAddresses
                        .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(ua => ua.Address.ToString())
                        .ToList();

                    var usbIP = ipv4Addresses.FirstOrDefault(ip =>
                        USB_TETHERING_PREFIXES.Any(prefix => ip.StartsWith(prefix)));

                    if (usbIP != null)
                    {
                        var gateway = ipProps.GatewayAddresses
                            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                        result.IsAvailable = true;
                        result.InterfaceName = ni.Name;
                        result.ServerIP = usbIP;
                        result.GatewayIP = gateway?.Address.ToString();
                        result.Description = ni.Description;

                        Console.WriteLine($"[USB-Tether] Found by IP range: {ni.Name} ({usbIP})");
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[USB-Tether] Detection error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Check if an IP address is in USB tethering range.
        /// </summary>
        public static bool IsUsbTetheringIP(string ip)
        {
            return USB_TETHERING_PREFIXES.Any(prefix => ip.StartsWith(prefix));
        }

        /// <summary>
        /// Get the USB tethering IP if available, otherwise return null.
        /// </summary>
        public static string? GetUsbTetheringIP()
        {
            var info = Detect();
            return info.IsAvailable ? info.ServerIP : null;
        }

        /// <summary>
        /// Print all network interfaces for debugging.
        /// </summary>
        public static void PrintAllInterfaces()
        {
            Console.WriteLine("[USB-Tether] All network interfaces:");
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                var ipProps = ni.GetIPProperties();
                var ipv4 = ipProps.UnicastAddresses
                    .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(ua => ua.Address.ToString())
                    .FirstOrDefault();

                Console.WriteLine($"  - {ni.Name}: {ipv4 ?? "no IPv4"} ({ni.Description})");
            }
        }
    }
}
