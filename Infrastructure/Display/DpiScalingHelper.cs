#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Display;

#if WINDOWS
/// <summary>
/// Helper để set/get DPI scaling sử dụng undocumented Windows API.
/// Dựa trên reverse engineering từ https://github.com/lihas/windows-DPI-scaling-sample
/// </summary>
static class DpiScalingHelper
{
    // DPI values supported by Windows (in percentage)
    private static readonly uint[] DpiVals = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    // Undocumented DISPLAYCONFIG_DEVICE_INFO_TYPE values
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;

    #region Native Structs

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)]
        public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(0)]
        public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public int size;
        public LUID adapterId;
        public uint id;
    }

    // Struct for getting DPI scale (size = 0x20 = 32 bytes)
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    // Struct for setting DPI scale (size = 0x18 = 24 bytes)
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int scaleRel;
    }

    #endregion

    #region Native Methods

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET setPacket);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;

    #endregion

    public struct DpiScalingInfo
    {
        public uint Minimum;
        public uint Maximum;
        public uint Current;
        public uint Recommended;
        public bool IsValid;
    }

    /// <summary>
    /// Get DPI scaling info for a specific display source
    /// </summary>
    public static DpiScalingInfo GetDpiScalingInfo(LUID adapterId, uint sourceId)
    {
        var info = new DpiScalingInfo { Minimum = 100, Maximum = 100, Current = 100, Recommended = 100, IsValid = false };

        var packet = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
                adapterId = adapterId,
                id = sourceId
            }
        };

        int result = DisplayConfigGetDeviceInfo(ref packet);
        if (result != ERROR_SUCCESS)
        {
            Logger.Error($"[DpiScaling] DisplayConfigGetDeviceInfo failed: {result}");
            return info;
        }

        // Clamp curScaleRel
        if (packet.curScaleRel < packet.minScaleRel)
            packet.curScaleRel = packet.minScaleRel;
        else if (packet.curScaleRel > packet.maxScaleRel)
            packet.curScaleRel = packet.maxScaleRel;

        int minAbs = Math.Abs(packet.minScaleRel);
        int totalSteps = minAbs + packet.maxScaleRel + 1;

        if (totalSteps <= DpiVals.Length)
        {
            info.Current = DpiVals[minAbs + packet.curScaleRel];
            info.Recommended = DpiVals[minAbs];
            info.Maximum = DpiVals[minAbs + packet.maxScaleRel];
            info.Minimum = 100;
            info.IsValid = true;
        }

        return info;
    }

    /// <summary>
    /// Set DPI scaling for a specific display source
    /// </summary>
    public static bool SetDpiScaling(LUID adapterId, uint sourceId, uint dpiPercent)
    {
        var currentInfo = GetDpiScalingInfo(adapterId, sourceId);
        if (!currentInfo.IsValid)
        {
            Logger.Error("[DpiScaling] Cannot get current DPI info");
            return false;
        }

        if (dpiPercent == currentInfo.Current)
        {
            Logger.Info($"[DpiScaling] Already at {dpiPercent}%");
            return true;
        }

        // Clamp to valid range
        if (dpiPercent < currentInfo.Minimum)
            dpiPercent = currentInfo.Minimum;
        else if (dpiPercent > currentInfo.Maximum)
            dpiPercent = currentInfo.Maximum;

        // Find indices in DpiVals array
        int targetIdx = -1, recommendedIdx = -1;
        for (int i = 0; i < DpiVals.Length; i++)
        {
            if (DpiVals[i] == dpiPercent) targetIdx = i;
            if (DpiVals[i] == currentInfo.Recommended) recommendedIdx = i;
        }

        if (targetIdx == -1 || recommendedIdx == -1)
        {
            Logger.Error($"[DpiScaling] Invalid DPI value: {dpiPercent}");
            return false;
        }

        int scaleRel = targetIdx - recommendedIdx;

        var packet = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(),
                adapterId = adapterId,
                id = sourceId
            },
            scaleRel = scaleRel
        };

        int result = DisplayConfigSetDeviceInfo(ref packet);
        if (result != ERROR_SUCCESS)
        {
            Logger.Error($"[DpiScaling] DisplayConfigSetDeviceInfo failed: {result}");
            return false;
        }

        Logger.Info($"[DpiScaling] ✓ Set DPI to {dpiPercent}%");
        return true;
    }

    /// <summary>
    /// Get all active display paths
    /// </summary>
    private static (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)? GetPathsAndModes()
    {
        uint numPaths, numModes;
        int status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes);
        if (status != ERROR_SUCCESS)
            return null;

        var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
        var modes = new DISPLAYCONFIG_MODE_INFO[numModes];

        status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
        if (status != ERROR_SUCCESS)
            return null;

        // Resize arrays to actual count
        Array.Resize(ref paths, (int)numPaths);
        Array.Resize(ref modes, (int)numModes);

        return (paths, modes);
    }

    /// <summary>
    /// Set DPI scaling for all active monitors
    /// </summary>
    public static bool SetAllMonitorsDpiScaling(uint dpiPercent)
    {
        var result = GetPathsAndModes();
        if (result == null)
        {
            Logger.Error("[DpiScaling] Failed to get display config");
            return false;
        }

        var (paths, _) = result.Value;
        bool allSuccess = true;

        Logger.Info($"[DpiScaling] Setting {dpiPercent}% for {paths.Length} monitor(s)...");

        foreach (var path in paths)
        {
            var info = GetDpiScalingInfo(path.sourceInfo.adapterId, path.sourceInfo.id);
            Logger.Info($"[DpiScaling] Monitor {path.sourceInfo.id}: current={info.Current}%, recommended={info.Recommended}%, max={info.Maximum}%");

            if (!SetDpiScaling(path.sourceInfo.adapterId, path.sourceInfo.id, dpiPercent))
            {
                allSuccess = false;
            }
        }

        return allSuccess;
    }

    /// <summary>
    /// Get current DPI scaling for all monitors
    /// </summary>
    public static List<(LUID adapterId, uint sourceId, DpiScalingInfo info)> GetAllMonitorsDpiInfo()
    {
        var list = new List<(LUID, uint, DpiScalingInfo)>();
        
        var result = GetPathsAndModes();
        if (result == null)
            return list;

        var (paths, _) = result.Value;
        foreach (var path in paths)
        {
            var info = GetDpiScalingInfo(path.sourceInfo.adapterId, path.sourceInfo.id);
            list.Add((path.sourceInfo.adapterId, path.sourceInfo.id, info));
        }

        return list;
    }
}
#endif
