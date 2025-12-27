#nullable enable
using System;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.DXGI;

namespace RemotePlayServer.Utils
{
    /// <summary>
    /// Gathers hardware information using WMI and DXGI.
    /// All methods are async to prevent blocking on slow WMI calls.
    /// </summary>
    public static class HardwareInfoGatherer
    {
        // Cache the hardware info since it rarely changes
        private static HardwareInfo? _cachedInfo;
        private static DateTime _lastGatherTime = DateTime.MinValue;
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(5);
        private static readonly object _cacheLock = new();

        /// <summary>
        /// Get hardware info, using cache if available and not expired.
        /// </summary>
        public static async Task<HardwareInfo> GetHardwareInfoAsync(bool forceRefresh = false)
        {
            lock (_cacheLock)
            {
                if (!forceRefresh && _cachedInfo != null &&
                    DateTime.UtcNow - _lastGatherTime < CacheExpiry)
                {
                    return _cachedInfo;
                }
            }

            var info = await GatherAllInfoAsync();

            lock (_cacheLock)
            {
                _cachedInfo = info;
                _lastGatherTime = DateTime.UtcNow;
            }

            return info;
        }

        /// <summary>
        /// Gather all hardware info in parallel for best performance.
        /// </summary>
        private static async Task<HardwareInfo> GatherAllInfoAsync()
        {
            var info = new HardwareInfo
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            // Run all gatherers in parallel
            var tasks = new Task[]
            {
                Task.Run(() => { info.DeviceName = GetDeviceName(); }),
                Task.Run(() => { info.Processor = GetProcessorInfo(); }),
                Task.Run(() => { info.Gpu = GetGpuInfo(); }),
                Task.Run(() => { info.Ram = GetRamInfo(); }),
                Task.Run(() => { info.Os = GetOsInfo(); }),
                Task.Run(() => { info.Network = GetNetworkAdapterInfo(); })
            };

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareInfo] Some info failed to gather: {ex.Message}");
            }

            return info;
        }

        // === Individual Gatherers ===

        public static string GetDeviceName()
        {
            try
            {
                return Environment.MachineName;
            }
            catch
            {
                return "Unknown";
            }
        }

        public static ProcessorInfo GetProcessorInfo()
        {
            var info = new ProcessorInfo
            {
                Cores = Environment.ProcessorCount,
                LogicalProcessors = Environment.ProcessorCount
            };

            try
            {
                // Use WMI for detailed CPU info
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");

                foreach (ManagementObject obj in searcher.Get())
                {
                    info.Name = obj["Name"]?.ToString()?.Trim() ?? "Unknown CPU";
                    info.Cores = Convert.ToInt32(obj["NumberOfCores"] ?? info.Cores);
                    info.LogicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? info.LogicalProcessors);
                    info.SpeedMHz = Convert.ToInt32(obj["MaxClockSpeed"] ?? 0);
                    break; // First processor only
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareInfo] CPU info via WMI failed: {ex.Message}");
                info.Name = "Unknown CPU";
            }

            return info;
        }

        public static GpuInfo GetGpuInfo()
        {
            var info = new GpuInfo();

            try
            {
                // Use DXGI for accurate GPU info (same pattern as EncoderFactory.cs)
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

                for (uint i = 0; ; i++)
                {
                    if (factory.EnumAdapters1(i, out var adapter).Failure) break;

                    try
                    {
                        var desc = adapter.Description;
                        string name = desc.Description;

                        // Skip Microsoft Basic Render Driver
                        if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        info.Name = name;
                        info.VendorId = (uint)desc.VendorId;

                        // Detect vendor from ID (most reliable)
                        info.Vendor = desc.VendorId switch
                        {
                            0x10DE => "NVIDIA",
                            0x1002 or 0x1022 => "AMD",
                            0x8086 => "Intel",
                            _ => DetectVendorFromName(name)
                        };

                        // Get VRAM from DXGI - DedicatedVideoMemory is in bytes
                        long vramBytes = (long)desc.DedicatedVideoMemory;
                        info.VramMB = vramBytes / (1024 * 1024);

                        // If VRAM is 0 or unreasonable, try WMI fallback
                        if (info.VramMB <= 0 || info.VramMB > 100_000)
                        {
                            info.VramMB = GetVramFromWmi() ?? 0;
                        }

                        // Get driver version from WMI
                        info.DriverVersion = GetGpuDriverVersion() ?? "";

                        break; // Use first valid discrete GPU
                    }
                    finally
                    {
                        adapter.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareInfo] GPU info via DXGI failed: {ex.Message}");
                // Fallback to WMI
                return GetGpuInfoFromWmi();
            }

            // If we still don't have GPU name, try WMI
            if (string.IsNullOrEmpty(info.Name))
            {
                return GetGpuInfoFromWmi();
            }

            return info;
        }

        /// <summary>
        /// Fallback: Get VRAM from WMI (can be inaccurate for >4GB due to 32-bit field).
        /// </summary>
        private static long? GetVramFromWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT AdapterRAM FROM Win32_VideoController WHERE AdapterRAM IS NOT NULL");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var ram = obj["AdapterRAM"];
                    if (ram != null)
                    {
                        // AdapterRAM is in bytes, but capped at 4GB on 32-bit field
                        long vramBytes = Convert.ToInt64(ram);
                        return vramBytes / (1024 * 1024);
                    }
                }
            }
            catch { }

            return null;
        }

        private static string? GetGpuDriverVersion()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DriverVersion FROM Win32_VideoController");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var version = obj["DriverVersion"]?.ToString();
                    if (!string.IsNullOrEmpty(version))
                        return version;
                }
            }
            catch { }

            return null;
        }

        private static GpuInfo GetGpuInfoFromWmi()
        {
            var info = new GpuInfo();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");

                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                        continue;

                    info.Name = name;
                    info.Vendor = DetectVendorFromName(name);
                    info.DriverVersion = obj["DriverVersion"]?.ToString() ?? "";

                    var ram = obj["AdapterRAM"];
                    if (ram != null)
                    {
                        info.VramMB = Convert.ToInt64(ram) / (1024 * 1024);
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareInfo] GPU WMI fallback failed: {ex.Message}");
            }

            return info;
        }

        private static string DetectVendorFromName(string name)
        {
            var upper = name.ToUpperInvariant();
            if (upper.Contains("NVIDIA") || upper.Contains("GEFORCE") ||
                upper.Contains("GTX") || upper.Contains("RTX"))
                return "NVIDIA";
            if (upper.Contains("AMD") || upper.Contains("RADEON") || upper.Contains("RX "))
                return "AMD";
            if (upper.Contains("INTEL") || upper.Contains("UHD") || upper.Contains("IRIS"))
                return "Intel";
            return "Unknown";
        }

        public static RamInfo GetRamInfo()
        {
            var info = new RamInfo();

            try
            {
                // Use native API for accurate memory info
                var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    info.TotalMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                    info.AvailableMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareInfo] RAM info via native API failed: {ex.Message}");
                // Fallback to WMI
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        // TotalVisibleMemorySize is in KB
                        info.TotalMB = Convert.ToInt64(obj["TotalVisibleMemorySize"] ?? 0) / 1024;
                        break;
                    }
                }
                catch { }
            }

            return info;
        }

        public static OsInfo GetOsInfo()
        {
            var info = new OsInfo
            {
                Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86"
            };

            try
            {
                // Use registry for accurate Windows version
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

                if (key != null)
                {
                    info.Name = key.GetValue("ProductName")?.ToString() ?? "Windows";
                    info.Version = key.GetValue("DisplayVersion")?.ToString() ??
                                   key.GetValue("ReleaseId")?.ToString() ?? "";

                    var buildNum = key.GetValue("CurrentBuildNumber")?.ToString() ?? "";
                    var ubr = key.GetValue("UBR")?.ToString() ?? "";
                    info.Build = string.IsNullOrEmpty(ubr) ? buildNum : $"{buildNum}.{ubr}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareInfo] OS info from registry failed: {ex.Message}");
                info.Name = Environment.OSVersion.VersionString;
            }

            return info;
        }

        public static NetworkAdapterInfo GetNetworkAdapterInfo()
        {
            var info = new NetworkAdapterInfo();

            try
            {
                // Find the best network interface (active, not virtual)
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    var nameDesc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    if (nameDesc.Contains("virtual") || nameDesc.Contains("vmware") ||
                        nameDesc.Contains("hyper-v") || nameDesc.Contains("loopback"))
                        continue;

                    var ipProps = ni.GetIPProperties();
                    var unicast = ipProps.UnicastAddresses.FirstOrDefault(
                        a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                             !System.Net.IPAddress.IsLoopback(a.Address) &&
                             !a.Address.ToString().StartsWith("169.254."));

                    if (unicast == null) continue;

                    // Determine connection type
                    info.ConnectionType = ni.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Ethernet => "Ethernet",
                        NetworkInterfaceType.GigabitEthernet => "Ethernet",
                        NetworkInterfaceType.Wireless80211 => "WiFi",
                        _ => "Unknown"
                    };

                    info.AdapterName = ni.Description;
                    info.SpeedMbps = ni.Speed / 1_000_000; // bits to megabits
                    info.IpAddress = unicast.Address.ToString();

                    break; // Use first valid interface
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareInfo] Network info failed: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// Get encoder info based on GPU vendor and available libraries.
        /// </summary>
        public static EncoderInfo GetEncoderInfo()
        {
            var info = new EncoderInfo();

            var gpuVendor = GpuVendorDetector.DetectPrimaryGpuVendor();

            switch (gpuVendor)
            {
                case GpuVendorDetector.GpuVendor.NVIDIA:
                    if (GpuVendorDetector.IsNvencAvailable())
                    {
                        info.Type = "NVENC";
                        info.HwAccel = true;
                    }
                    break;

                case GpuVendorDetector.GpuVendor.AMD:
                    if (GpuVendorDetector.IsAmfAvailable())
                    {
                        info.Type = "AMF";
                        info.HwAccel = true;
                    }
                    break;

                case GpuVendorDetector.GpuVendor.Intel:
                    // Check for QSV
                    info.Type = "QSV";
                    info.HwAccel = true;
                    break;
            }

            if (!info.HwAccel)
            {
                info.Type = "Software";
                info.HwAccel = false;
            }

            return info;
        }

        // === Native API for Memory ===

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
