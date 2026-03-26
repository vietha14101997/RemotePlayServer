#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.DXGI;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Hardware
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
                Logger.Error($"[HardwareInfo] Some info failed to gather: {ex.Message}");
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
                Logger.Error($"[HardwareInfo] CPU info via WMI failed: {ex.Message}");
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

                        // Skip virtual/software adapters
                        if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
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

                        // Get VRAM from DXGI - DedicatedVideoMemory is nuint (bytes)
                        // Use unchecked to prevent overflow on large values
                        ulong vramBytes = (ulong)desc.DedicatedVideoMemory;
                        info.VramMB = (long)(vramBytes / (1024 * 1024));

                        Logger.Info($"[HardwareInfo] DXGI VRAM: {vramBytes} bytes = {info.VramMB} MB");

                        // If VRAM is 0 or unreasonable, try registry/WMI fallback
                        if (info.VramMB <= 0 || info.VramMB > 100_000)
                        {
                            info.VramMB = GetVramFromRegistry(name) ?? GetVramFromWmi() ?? 0;
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
                Logger.Error($"[HardwareInfo] GPU info via DXGI failed: {ex.Message}");
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
        /// Get VRAM from Windows Registry (more accurate for >4GB).
        /// </summary>
        private static long? GetVramFromRegistry(string gpuName)
        {
            try
            {
                // Try to read from Display adapter registry keys
                using var displayKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");

                if (displayKey != null)
                {
                    foreach (var subKeyName in displayKey.GetSubKeyNames())
                    {
                        if (!int.TryParse(subKeyName, out _)) continue;

                        using var subKey = displayKey.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var driverDesc = subKey.GetValue("DriverDesc")?.ToString() ?? "";

                        // Match GPU name
                        if (string.IsNullOrEmpty(gpuName) ||
                            driverDesc.Contains(gpuName.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                        {
                            // Try HardwareInformation.qwMemorySize (QWORD, accurate for large VRAM)
                            var qwMemSize = subKey.GetValue("HardwareInformation.qwMemorySize");
                            if (qwMemSize != null)
                            {
                                long vramBytes = Convert.ToInt64(qwMemSize);
                                Logger.Info($"[HardwareInfo] Registry qwMemorySize: {vramBytes} bytes");
                                return vramBytes / (1024 * 1024);
                            }

                            // Fallback to HardwareInformation.MemorySize (DWORD, limited to 4GB)
                            var memSize = subKey.GetValue("HardwareInformation.MemorySize");
                            if (memSize != null)
                            {
                                long vramBytes = Convert.ToInt64(memSize);
                                Logger.Info($"[HardwareInfo] Registry MemorySize: {vramBytes} bytes");
                                return vramBytes / (1024 * 1024);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HardwareInfo] Registry VRAM read failed: {ex.Message}");
            }

            return null;
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
                        name.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
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
                Logger.Error($"[HardwareInfo] GPU WMI fallback failed: {ex.Message}");
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
                Logger.Error($"[HardwareInfo] RAM info via native API failed: {ex.Message}");
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

                    // Windows 11 detection: Build 22000+ is Windows 11
                    // Registry ProductName may still say "Windows 10" on Windows 11
                    if (int.TryParse(buildNum, out int build) && build >= 22000)
                    {
                        // Replace "Windows 10" with "Windows 11" in ProductName
                        info.Name = info.Name.Replace("Windows 10", "Windows 11");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HardwareInfo] OS info from registry failed: {ex.Message}");
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
                Logger.Error($"[HardwareInfo] Network info failed: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// Get encoder info based on GPU vendor and available libraries.
        /// </summary>
        public static EncoderInfo GetEncoderInfo()
        {
            var info = new EncoderInfo();
            info.SupportedCodecs = new List<string> { "H264" }; // H.264 always supported

            var gpuVendor = GpuVendorDetector.DetectPrimaryGpuVendor();
            var gpuInfo = GetGpuInfo();

            switch (gpuVendor)
            {
                case GpuVendorDetector.GpuVendor.NVIDIA:
                    if (GpuVendorDetector.IsNvencAvailable())
                    {
                        info.Type = "NVENC";
                        info.HwAccel = true;

                        bool hevcSupported = CheckHevcEncoderAvailable("hevc_nvenc");

                        // Only use name-based fallback if trial encode couldn't run at all
                        if (!hevcSupported && !IsTrialEncodeResultCached("hevc_nvenc"))
                        {
                            hevcSupported = CheckNvidiaHevcSupport(gpuInfo.Name);
                            Logger.Info($"[HardwareInfo] HEVC trial encode unavailable, GPU name heuristic: {hevcSupported}");
                        }

                        if (hevcSupported)
                        {
                            info.SupportedCodecs.Add("H265");
                            info.SupportsHevc = true;
                        }
                    }
                    break;

                case GpuVendorDetector.GpuVendor.AMD:
                    if (GpuVendorDetector.IsAmfAvailable())
                    {
                        info.Type = "AMF";
                        info.HwAccel = true;

                        bool hevcSupported = CheckHevcEncoderAvailable("hevc_amf");

                        // Only use name-based fallback if trial encode couldn't run at all
                        if (!hevcSupported && !IsTrialEncodeResultCached("hevc_amf"))
                        {
                            hevcSupported = CheckAmdHevcSupport(gpuInfo.Name);
                            Logger.Info($"[HardwareInfo] HEVC trial encode unavailable, GPU name heuristic: {hevcSupported}");
                        }

                        if (hevcSupported)
                        {
                            info.SupportedCodecs.Add("H265");
                            info.SupportsHevc = true;
                        }
                    }
                    break;

                case GpuVendorDetector.GpuVendor.Intel:
                    info.Type = "QSV";
                    info.HwAccel = true;

                    // Trial encode is authoritative — if it ran and failed, the hardware
                    // truly cannot encode HEVC regardless of GPU generation.
                    // Only fall back to name-based check if FFmpeg DLLs are missing
                    // (trial encode couldn't run at all).
                    bool intelHevcSupported = CheckHevcEncoderAvailable("hevc_qsv");

                    if (!intelHevcSupported && !IsTrialEncodeResultCached("hevc_qsv"))
                    {
                        // Trial encode didn't run (e.g., FFmpeg not available) — use GPU name heuristic
                        intelHevcSupported = CheckIntelHevcSupport(gpuInfo.Name);
                        Logger.Info($"[HardwareInfo] HEVC trial encode unavailable, GPU name heuristic: {intelHevcSupported}");
                    }

                    if (intelHevcSupported)
                    {
                        info.SupportedCodecs.Add("H265");
                        info.SupportsHevc = true;
                    }
                    break;
            }

            if (!info.HwAccel)
            {
                info.Type = "Software";
                info.HwAccel = false;
            }

            // Set preferred codec - H.265 if supported, otherwise H.264
            info.PreferredCodec = info.SupportsHevc ? "H265" : "H264";

            Logger.Info($"[HardwareInfo] Encoder: {info.Type}, HwAccel: {info.HwAccel}, HEVC: {info.SupportsHevc}, Codecs: [{string.Join(", ", info.SupportedCodecs)}]");

            return info;
        }

        /// <summary>
        /// Check NVIDIA GPU HEVC support based on GPU name.
        /// Maxwell (GTX 900), Pascal (GTX 1000), Turing (GTX 1600, RTX 2000), Ampere (RTX 3000), Ada (RTX 4000) support HEVC.
        /// </summary>
        private static bool CheckNvidiaHevcSupport(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return false;

            var upper = gpuName.ToUpperInvariant();

            // RTX series (all support HEVC)
            if (upper.Contains("RTX")) return true;

            // GTX 1600 series (Turing, supports HEVC)
            if (upper.Contains("GTX 16")) return true;

            // GTX 1000 series (Pascal, supports HEVC)
            if (upper.Contains("GTX 10")) return true;

            // GTX 900 series (Maxwell, supports HEVC except 900M mobile might be limited)
            if (upper.Contains("GTX 9") && !upper.Contains("900M")) return true;

            // Quadro Pascal/Turing/Ampere
            if (upper.Contains("QUADRO") && (upper.Contains("P") || upper.Contains("RTX"))) return true;

            // Tesla P/V/A series
            if (upper.Contains("TESLA") && (upper.Contains(" P") || upper.Contains(" V") || upper.Contains(" A"))) return true;

            Logger.Info($"[HardwareInfo] GPU '{gpuName}' - HEVC support unknown, assuming no");
            return false;
        }

        /// <summary>
        /// Check AMD GPU HEVC support based on GPU name.
        /// Polaris (RX 400/500), Vega, Navi (RX 5000/6000/7000) support HEVC.
        /// </summary>
        private static bool CheckAmdHevcSupport(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return false;

            var upper = gpuName.ToUpperInvariant();

            // RX 7000 series (RDNA 3)
            if (upper.Contains("RX 7")) return true;

            // RX 6000 series (RDNA 2)
            if (upper.Contains("RX 6")) return true;

            // RX 5000 series (RDNA 1)
            if (upper.Contains("RX 5")) return true;

            // RX Vega
            if (upper.Contains("VEGA")) return true;

            // RX 400/500 series (Polaris)
            if (upper.Contains("RX 4") || upper.Contains("RX 5")) return true;

            Logger.Info($"[HardwareInfo] GPU '{gpuName}' - HEVC support unknown, assuming no");
            return false;
        }

        /// <summary>
        /// Check Intel GPU HEVC support based on GPU name.
        /// Skylake (6th gen) and newer support HEVC.
        /// </summary>
        private static bool CheckIntelHevcSupport(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return false;

            var upper = gpuName.ToUpperInvariant();

            // Arc series (all support HEVC)
            if (upper.Contains("ARC")) return true;

            // Iris Xe (11th gen+)
            if (upper.Contains("IRIS XE") || upper.Contains("IRIS(R) XE")) return true;

            // Iris Plus (10th gen)
            if (upper.Contains("IRIS PLUS")) return true;

            // UHD Graphics 6xx (8th-10th gen, support HEVC)
            if (upper.Contains("UHD") && (upper.Contains("6") || upper.Contains("7"))) return true;

            // HD Graphics 5xx/6xx (6th-7th gen Skylake/Kaby Lake, support HEVC)
            if (upper.Contains("HD GRAPHICS 5") || upper.Contains("HD GRAPHICS 6")) return true;

            Logger.Info($"[HardwareInfo] GPU '{gpuName}' - HEVC support unknown, assuming no");
            return false;
        }

        // FFmpeg initialization flag
        private static bool _ffmpegInitialized = false;
        private static readonly object _ffmpegInitLock = new();

        // P/Invoke for SetDllDirectory to add FFmpeg DLLs to search path
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        /// <summary>
        /// Initialize FFmpeg library if not already done.
        /// </summary>
        private static void EnsureFfmpegInitialized()
        {
            lock (_ffmpegInitLock)
            {
                if (_ffmpegInitialized) return;

                try
                {
                    // Try to find FFmpeg libraries in common locations
                    string? ffmpegPath = null;

                    // Check common paths - IMPORTANT: "bin" subfolder is where FFmpeg DLLs are located
                    var possiblePaths = new[]
                    {
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin"), // Primary location
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg-master-latest-win64-gpl-shared", "bin"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"),
                        @"C:\ffmpeg\bin",
                        AppDomain.CurrentDomain.BaseDirectory // Current directory
                    };

                    foreach (var path in possiblePaths)
                    {
                        if (Directory.Exists(path) &&
                            (File.Exists(Path.Combine(path, "avcodec-61.dll")) ||
                             File.Exists(Path.Combine(path, "avcodec-60.dll")) ||
                             File.Exists(Path.Combine(path, "avcodec-59.dll")) ||
                             File.Exists(Path.Combine(path, "avcodec.dll"))))
                        {
                            ffmpegPath = path;
                            break;
                        }
                    }

                    if (ffmpegPath != null)
                    {
                        // CRITICAL: Add FFmpeg folder to Windows DLL search path
                        // This is required because FFmpeg.AutoGen uses DllImport
                        if (SetDllDirectory(ffmpegPath))
                        {
                            Logger.Info($"[HardwareInfo] SetDllDirectory: {ffmpegPath}");
                        }
                        else
                        {
                            Logger.Error($"[HardwareInfo] SetDllDirectory failed, error: {Marshal.GetLastWin32Error()}");
                        }

                        // Also set FFmpeg.AutoGen RootPath
                        FFmpeg.AutoGen.ffmpeg.RootPath = ffmpegPath;
                        Logger.Info($"[HardwareInfo] FFmpeg.RootPath: {ffmpegPath}");

                        // Add to PATH as fallback
                        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                        if (!currentPath.Contains(ffmpegPath))
                        {
                            Environment.SetEnvironmentVariable("PATH", ffmpegPath + ";" + currentPath);
                            Logger.Info($"[HardwareInfo] Added to PATH: {ffmpegPath}");
                        }
                    }
                    else
                    {
                        // Try without setting RootPath (use system PATH)
                        Logger.Error("[HardwareInfo] FFmpeg path not found, trying system PATH");
                    }

                    _ffmpegInitialized = true;
                }
                catch (Exception ex)
                {
                    Logger.Info($"[HardwareInfo] FFmpeg initialization warning: {ex.Message}");
                    _ffmpegInitialized = true; // Mark as initialized to avoid repeated attempts
                }
            }
        }

        // Cache trial encode results per encoder name (persists across connections within same process)
        private static readonly Dictionary<string, bool> _hevcProbeCache = new();
        private static readonly object _hevcProbeLock = new();

        /// <summary>
        /// Check if a specific HEVC encoder is actually usable via FFmpeg trial encode.
        /// Unlike just finding the codec by name, this actually tries to open the encoder
        /// with real parameters to verify the hardware supports it at runtime.
        /// Results are cached per-process to avoid repeating slow probes.
        /// </summary>
        private static unsafe bool CheckHevcEncoderAvailable(string encoderName)
        {
            // Check cache first
            lock (_hevcProbeLock)
            {
                if (_hevcProbeCache.TryGetValue(encoderName, out bool cached))
                {
                    Logger.Info($"[HardwareInfo] HEVC encoder '{encoderName}': {(cached ? "available" : "not supported")} (cached)");
                    return cached;
                }
            }

            bool result = false;
            try
            {
                EnsureFfmpegInitialized();

                var codec = FFmpeg.AutoGen.ffmpeg.avcodec_find_encoder_by_name(encoderName);
                if (codec == null)
                {
                    Logger.Info($"[HardwareInfo] HEVC encoder '{encoderName}': not found");
                    CacheHevcProbeResult(encoderName, false);
                    return false;
                }

                Logger.Info($"[HardwareInfo] HEVC encoder '{encoderName}': found, verifying with trial encode...");
                result = TryTrialEncode(codec, encoderName);
                Logger.Info($"[HardwareInfo] HEVC encoder '{encoderName}': trial encode {(result ? "PASSED" : "FAILED")}");
            }
            catch (DllNotFoundException ex)
            {
                Logger.Error($"[HardwareInfo] FFmpeg DLL not found for '{encoderName}': {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[HardwareInfo] Failed to check HEVC encoder '{encoderName}': {ex.Message}");
            }

            CacheHevcProbeResult(encoderName, result);
            return result;
        }

        private static void CacheHevcProbeResult(string encoderName, bool result)
        {
            lock (_hevcProbeLock)
            {
                _hevcProbeCache[encoderName] = result;
            }
        }

        /// <summary>
        /// Check if trial encode result is cached (i.e., trial encode actually ran).
        /// Used to decide whether name-based GPU heuristic should be used as fallback.
        /// </summary>
        private static bool IsTrialEncodeResultCached(string encoderName)
        {
            lock (_hevcProbeLock)
            {
                return _hevcProbeCache.ContainsKey(encoderName);
            }
        }

        /// <summary>
        /// Try to actually open and configure the HEVC encoder to verify hardware support.
        /// Uses minimal 320x240 resolution to keep the probe fast.
        /// </summary>
        private static unsafe bool TryTrialEncode(FFmpeg.AutoGen.AVCodec* codec, string encoderName)
        {
            FFmpeg.AutoGen.AVCodecContext* ctx = null;
            try
            {
                ctx = FFmpeg.AutoGen.ffmpeg.avcodec_alloc_context3(codec);
                if (ctx == null)
                {
                    Logger.Error($"[HardwareInfo] Trial encode: failed to alloc context for '{encoderName}'");
                    return false;
                }

                // Minimal parameters — just enough to test if the encoder can open
                ctx->width = 320;
                ctx->height = 240;
                ctx->time_base = new FFmpeg.AutoGen.AVRational { num = 1, den = 30 };
                ctx->framerate = new FFmpeg.AutoGen.AVRational { num = 30, den = 1 };
                ctx->pix_fmt = FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NV12;
                ctx->bit_rate = 1_000_000;
                ctx->gop_size = 30;
                ctx->max_b_frames = 0;
                ctx->flags |= FFmpeg.AutoGen.ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                ctx->thread_count = 1;

                // HEVC profile/level
                ctx->profile = FFmpeg.AutoGen.ffmpeg.FF_PROFILE_HEVC_MAIN;
                ctx->level = 120; // Level 4.0

                // Set encoder-specific options based on name
                if (encoderName.Contains("qsv"))
                {
                    FFmpeg.AutoGen.ffmpeg.av_opt_set(ctx->priv_data, "preset", "faster", 0);
                    FFmpeg.AutoGen.ffmpeg.av_opt_set(ctx->priv_data, "low_power", "1", 0);
                    FFmpeg.AutoGen.ffmpeg.av_opt_set(ctx->priv_data, "async_depth", "1", 0);

                    // Try to create QSV hardware device for the probe
                    FFmpeg.AutoGen.AVBufferRef* hwDeviceCtx = null;
                    int hwRet = FFmpeg.AutoGen.ffmpeg.av_hwdevice_ctx_create(
                        &hwDeviceCtx,
                        FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
                        "auto", null, 0);
                    if (hwRet >= 0 && hwDeviceCtx != null)
                    {
                        ctx->hw_device_ctx = FFmpeg.AutoGen.ffmpeg.av_buffer_ref(hwDeviceCtx);
                        FFmpeg.AutoGen.ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    }
                }
                else if (encoderName.Contains("nvenc"))
                {
                    FFmpeg.AutoGen.ffmpeg.av_opt_set(ctx->priv_data, "preset", "p1", 0);
                    FFmpeg.AutoGen.ffmpeg.av_opt_set(ctx->priv_data, "tune", "ull", 0);
                }
                else if (encoderName.Contains("amf"))
                {
                    FFmpeg.AutoGen.ffmpeg.av_opt_set(ctx->priv_data, "usage", "ultralowlatency", 0);
                    FFmpeg.AutoGen.ffmpeg.av_opt_set(ctx->priv_data, "quality", "speed", 0);
                }

                int ret = FFmpeg.AutoGen.ffmpeg.avcodec_open2(ctx, codec, null);
                if (ret < 0)
                {
                    Logger.Warn($"[HardwareInfo] Trial encode: avcodec_open2 failed for '{encoderName}' (error={ret})");
                    return false;
                }

                // Encoder opened successfully — hardware truly supports HEVC
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[HardwareInfo] Trial encode exception for '{encoderName}': {ex.Message}");
                return false;
            }
            finally
            {
                if (ctx != null)
                {
                    FFmpeg.AutoGen.ffmpeg.avcodec_free_context(&ctx);
                }
            }
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
