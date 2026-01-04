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
}
