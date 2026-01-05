#nullable enable
using System;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace RemotePlayServer.Utils;

/// <summary>
/// GPU vendor detection and hardware availability checks
/// </summary>
public static class GpuVendorDetector
{
    public enum GpuVendor
    {
        Unknown,
        NVIDIA,
        AMD,
        Intel
    }

    /// <summary>
    /// Intel driver version info with MF encoder compatibility assessment
    /// </summary>
    public class IntelDriverInfo
    {
        public Version? DriverVersion { get; set; }
        public string DriverVersionString { get; set; } = "";
        public string GpuName { get; set; } = "";
        public bool SupportsMFHardwareEncoder { get; set; }
        public string RecommendedEncoder { get; set; } = "software";
        public string Reason { get; set; } = "";
    }

    // Minimum driver version for reliable Media Foundation H.264 hardware encoder
    // Intel DCH drivers 27.20.100.x (2020+) have stable MF hardware encoder support
    private static readonly Version MinMFDriverVersion = new Version(27, 20, 100, 0);

    // Older drivers may work with FFmpeg QSV (uses Intel Media SDK directly)
    private static readonly Version MinQsvDriverVersion = new Version(26, 20, 100, 0);

    /// <summary>
    /// Detect the primary GPU vendor from DXGI adapters
    /// </summary>
    public static GpuVendor DetectPrimaryGpuVendor()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure) break;

                try
                {
                    var desc = adapter.Description;
                    uint vendorId = (uint)desc.VendorId;

                    // Check by vendor ID (most reliable)
                    if (vendorId == 0x10DE) // NVIDIA
                    {
                        Console.WriteLine($"[GpuVendorDetector] Detected NVIDIA GPU: {desc.Description}");
                        return GpuVendor.NVIDIA;
                    }
                    else if (vendorId == 0x1002 || vendorId == 0x1022) // AMD/ATI
                    {
                        Console.WriteLine($"[GpuVendorDetector] Detected AMD GPU: {desc.Description}");
                        return GpuVendor.AMD;
                    }
                    else if (vendorId == 0x8086) // Intel
                    {
                        Console.WriteLine($"[GpuVendorDetector] Detected Intel GPU: {desc.Description}");
                        return GpuVendor.Intel;
                    }

                    // Fallback to name check
                    string name = desc.Description.ToUpperInvariant();
                    if (name.Contains("NVIDIA") || name.Contains("GEFORCE") || name.Contains("RTX") || name.Contains("GTX"))
                        return GpuVendor.NVIDIA;
                    else if (name.Contains("AMD") || name.Contains("RADEON") || name.Contains("ATI"))
                        return GpuVendor.AMD;
                    else if (name.Contains("INTEL"))
                        return GpuVendor.Intel;
                }
                finally
                {
                    adapter.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuVendorDetector] Detection failed: {ex.Message}");
        }

        return GpuVendor.Unknown;
    }

    public static bool IsAmfAvailable()
    {
        try
        {
            IntPtr handle = NativeLibrary.Load("amfrt64.dll");
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool IsNvencAvailable()
    {
        try
        {
            IntPtr handle = NativeLibrary.Load("nvEncodeAPI64.dll");
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Get Intel GPU driver info and determine encoder compatibility
    /// </summary>
    public static IntelDriverInfo GetIntelDriverInfo()
    {
        var info = new IntelDriverInfo();

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure) break;

                try
                {
                    var desc = adapter.Description;
                    uint vendorId = (uint)desc.VendorId;

                    // Only process Intel GPUs
                    if (vendorId != 0x8086) continue;

                    info.GpuName = desc.Description;

                    // Try to get driver version from Windows registry
                    var driverVersion = GetIntelDriverVersionFromRegistry(desc.Description);
                    if (driverVersion != null)
                    {
                        info.DriverVersion = driverVersion;
                        info.DriverVersionString = driverVersion.ToString();

                        // Assess MF encoder compatibility
                        if (driverVersion >= MinMFDriverVersion)
                        {
                            info.SupportsMFHardwareEncoder = true;
                            info.RecommendedEncoder = "qsv_native";
                            info.Reason = $"Driver {driverVersion} >= {MinMFDriverVersion} (MF Hardware Encoder supported)";
                        }
                        else if (driverVersion >= MinQsvDriverVersion)
                        {
                            info.SupportsMFHardwareEncoder = false;
                            info.RecommendedEncoder = "qsv_ffmpeg";
                            info.Reason = $"Driver {driverVersion} < {MinMFDriverVersion} (use FFmpeg QSV instead)";
                        }
                        else
                        {
                            info.SupportsMFHardwareEncoder = false;
                            info.RecommendedEncoder = "software";
                            info.Reason = $"Driver {driverVersion} too old (< {MinQsvDriverVersion})";
                        }
                    }
                    else
                    {
                        // Can't determine driver version - try native first, fallback if fails
                        info.DriverVersionString = "unknown";
                        info.SupportsMFHardwareEncoder = false; // Assume not supported
                        info.RecommendedEncoder = "qsv_try_native";
                        info.Reason = "Driver version unknown - will try native MF first";
                    }

                    Console.WriteLine($"[GpuVendorDetector] Intel driver: {info.DriverVersionString}, recommended: {info.RecommendedEncoder}");
                    return info;
                }
                finally
                {
                    adapter.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuVendorDetector] Intel driver detection failed: {ex.Message}");
            info.Reason = $"Detection error: {ex.Message}";
        }

        return info;
    }

    /// <summary>
    /// Get Intel driver version from Windows registry
    /// </summary>
    private static Version? GetIntelDriverVersionFromRegistry(string gpuName)
    {
        try
        {
            // Search in HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}
            // This is the display adapter class GUID
            string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(classKey);
            if (key == null) return null;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                if (!char.IsDigit(subKeyName[0])) continue; // Skip non-numeric subkeys

                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var driverDesc = subKey.GetValue("DriverDesc") as string;
                var providerName = subKey.GetValue("ProviderName") as string;

                // Check if this is an Intel adapter
                bool isIntel = (providerName?.Contains("Intel", StringComparison.OrdinalIgnoreCase) ?? false) ||
                               (driverDesc?.Contains("Intel", StringComparison.OrdinalIgnoreCase) ?? false);

                if (!isIntel) continue;

                // Get driver version
                var driverVersion = subKey.GetValue("DriverVersion") as string;
                if (!string.IsNullOrEmpty(driverVersion))
                {
                    if (Version.TryParse(driverVersion, out var version))
                    {
                        return version;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuVendorDetector] Registry query failed: {ex.Message}");
        }

        return null;
    }
}
