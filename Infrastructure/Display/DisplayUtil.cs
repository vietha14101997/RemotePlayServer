#nullable enable
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Display;

static class DisplayUtil
{
    // --- Constants & flags ---
    const int ENUM_CURRENT_SETTINGS = -1;
    const int DM_POSITION = 0x00000020;
    const int DM_PELSWIDTH = 0x00080000;
    const int DM_PELSHEIGHT = 0x00100000;
    const int DM_DISPLAYFREQUENCY = 0x00400000;

    const int CDS_UPDATEREGISTRY = 0x00000001;
    const int CDS_GLOBAL = 0x00000008;

    const int DISP_CHANGE_SUCCESSFUL = 0;
    const int DISPLAY_DEVICE_ACTIVE = 0x00000001;
    const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    // --- Structs & P/Invoke ---
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;

        public int dmPositionX;
        public int dmPositionY;
        public ScreenOrientation dmDisplayOrientation;
        public int dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    public struct DisplayModeSnapshot
    {
        public string DeviceName { get; set; }   // \\.\DISPLAYx
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Frequency { get; set; }
    }

    public static bool TryGetMode(string deviceName, out DisplayModeSnapshot snap)
    {
        var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
        {
            snap = default;
            return false;
        }
        snap = new DisplayModeSnapshot
        {
            DeviceName = deviceName,
            X = dm.dmPositionX,
            Y = dm.dmPositionY,
            Width = dm.dmPelsWidth,
            Height = dm.dmPelsHeight,
            Frequency = dm.dmDisplayFrequency
        };
        return true;
    }

    public static List<DisplayModeSnapshot> SnapshotAll(IEnumerable<string> deviceNames)
    {
        var list = new List<DisplayModeSnapshot>();
        foreach (var dn in deviceNames)
            if (TryGetMode(dn, out var s)) list.Add(s);
        return list;
    }

    public static void SaveSnapshot(string path, List<DisplayModeSnapshot> snaps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snaps));
    }

    public static DisplayModeSnapshot GetCurrentMode(string deviceName)
    {
        if (TryGetMode(deviceName, out var snap))
            return snap;
        return new DisplayModeSnapshot { DeviceName = deviceName, Width = 1920, Height = 1080, Frequency = 60 };
    }

    /// <summary>
    /// Get the maximum supported refresh rate for a monitor at its current resolution.
    /// Enumerates all display modes and finds the highest Hz at current width/height.
    /// </summary>
    public static int GetMaxRefreshRate(string deviceName)
    {
        var current = GetCurrentMode(deviceName);
        int maxHz = current.Frequency;

        for (int i = 0; ; i++)
        {
            var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsEx(deviceName, i, ref dm, 0)) break;

            if (dm.dmPelsWidth == current.Width && dm.dmPelsHeight == current.Height && dm.dmDisplayFrequency > maxHz)
                maxHz = dm.dmDisplayFrequency;
        }

        return maxHz;
    }

    public static List<DisplayModeSnapshot>? LoadSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<List<DisplayModeSnapshot>>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static void RestoreFromSnapshot(List<DisplayModeSnapshot> snaps)
    {
        if (snaps == null) return;
        foreach (var s in snaps)
        {
            // Khôi phục độ phân giải & tần số; layout X/Y để Windows tự hàn gắn lại theo registry hiện tại
            try { ForceResolution(s.DeviceName, s.Width, s.Height, s.Frequency); } catch { }
        }
    }

    // --- Helpers ---

    /// Đọc layout (tọa độ X/Y + kích thước) hiện tại của một \\.\DISPLAYx
    public static (int x, int y, int w, int h, bool ok) TryGetLayout(string deviceName)
    {
        var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
            return (0, 0, 0, 0, false);
        return (dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight, true);
    }

    /// Đặt mode độ phân giải / tần số cho một \\.\DISPLAYx
    public static bool ForceResolution(string deviceName, int w, int h, int hz)
    {
        var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
        {
            Logger.Error($"[DisplayUtil] ForceResolution: EnumDisplaySettingsEx(CURRENT) failed for {deviceName}");
            return false;
        }

        Logger.Info($"[DisplayUtil] ForceResolution: {deviceName} current={dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}Hz, target={w}x{h}@{hz}Hz");

        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmDisplayFrequency = hz;

        int ret = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_GLOBAL, IntPtr.Zero);
        if (ret != DISP_CHANGE_SUCCESSFUL)
            Logger.Error($"[DisplayUtil] ForceResolution: ChangeDisplaySettingsEx returned {ret} for {deviceName}");
        return ret == DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>
    /// Enumerate all available display modes for a device.
    /// Logs all modes and returns whether the target mode was found.
    /// </summary>
    public static bool EnumerateAndLogModes(string deviceName, int targetW = 0, int targetH = 0, int targetHz = 0)
    {
        var modes = new List<(int w, int h, int hz)>();
        bool targetFound = false;

        for (int i = 0; ; i++)
        {
            var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsEx(deviceName, i, ref dm, 0))
                break;

            // Deduplicate
            var mode = (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
            if (!modes.Contains(mode))
            {
                modes.Add(mode);
                if (targetW > 0 && mode.dmPelsWidth == targetW && mode.dmPelsHeight == targetH && mode.dmDisplayFrequency == targetHz)
                    targetFound = true;
            }
        }

        Logger.Info($"[DisplayUtil] Available modes for {deviceName}: {modes.Count} unique modes");
        foreach (var (mw, mh, mhz) in modes)
        {
            string marker = (targetW > 0 && mw == targetW && mh == targetH && mhz == targetHz) ? " <<<TARGET>>>" : "";
            Logger.Info($"[DisplayUtil]   {mw}x{mh}@{mhz}Hz{marker}");
        }

        if (targetW > 0)
            Logger.Info($"[DisplayUtil] Target {targetW}x{targetH}@{targetHz}Hz: {(targetFound ? "FOUND" : "NOT FOUND")}");

        return targetFound;
    }

    /// <summary>
    /// Force resolution using mode index matching instead of CDS_GLOBAL.
    /// Enumerates available modes, finds matching mode, and applies it.
    /// This is more reliable after topology changes.
    /// </summary>
    public static bool ForceResolutionViaModeEnum(string deviceName, int w, int h, int hz)
    {
        // Find the exact mode from the driver's mode list
        for (int i = 0; ; i++)
        {
            var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsEx(deviceName, i, ref dm, 0))
                break;

            if (dm.dmPelsWidth == w && dm.dmPelsHeight == h && dm.dmDisplayFrequency == hz)
            {
                Logger.Info($"[DisplayUtil] ForceResolutionViaEnum: Found mode at index {i}: {w}x{h}@{hz}Hz");

                // Try applying with just CDS_UPDATEREGISTRY (no CDS_GLOBAL)
                int ret = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
                if (ret == DISP_CHANGE_SUCCESSFUL)
                {
                    Logger.Info($"[DisplayUtil] ForceResolutionViaEnum: Applied successfully via CDS_UPDATEREGISTRY");
                    return true;
                }
                Logger.Info($"[DisplayUtil] ForceResolutionViaEnum: CDS_UPDATEREGISTRY returned {ret}, trying without flags...");

                // Try with no flags (temporary change)
                ret = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
                if (ret == DISP_CHANGE_SUCCESSFUL)
                {
                    Logger.Info($"[DisplayUtil] ForceResolutionViaEnum: Applied successfully via temporary change");
                    return true;
                }
                Logger.Error($"[DisplayUtil] ForceResolutionViaEnum: All attempts failed, last error={ret}");
                return false;
            }
        }

        Logger.Error($"[DisplayUtil] ForceResolutionViaEnum: Mode {w}x{h}@{hz}Hz not found in mode list");
        return false;
    }

    /// <summary>
    /// Set resolution và position cho một \\.\DISPLAYx
    /// Position cho phép đặt monitor ở vị trí cụ thể trong desktop topology
    /// </summary>
    public static bool SetResolutionAndPosition(string deviceName, int w, int h, int hz, int posX, int posY)
    {
        const int CDS_NORESET = 0x10000000;

        var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
        {
            Logger.Error($"[DisplayUtil] SetResolutionAndPosition: EnumDisplaySettingsEx failed for {deviceName}");
            return false;
        }

        // Log current position before change
        Logger.Info($"[DisplayUtil] {deviceName} current position: ({dm.dmPositionX},{dm.dmPositionY})");

        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        dm.dmPositionX = posX;
        dm.dmPositionY = posY;
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmDisplayFrequency = hz;

        // First call with CDS_NORESET to queue the change
        int ret = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (ret != DISP_CHANGE_SUCCESSFUL)
        {
            Logger.Error($"[DisplayUtil] SetResolutionAndPosition FAILED for {deviceName}: error code {ret}");
            return false;
        }
        Logger.Info($"[DisplayUtil] SetResolutionAndPosition QUEUED for {deviceName}: ({posX},{posY}) {w}x{h}@{hz}Hz");
        return true;
    }

    /// <summary>
    /// Apply all pending display changes (call after SetResolutionAndPosition for each monitor)
    /// </summary>
    public static bool ApplyDisplayChanges()
    {
        // Pass null for both device name and DEVMODE to apply all pending changes
        int ret = ChangeDisplaySettingsExNull(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (ret == DISP_CHANGE_SUCCESSFUL)
        {
            Logger.Info($"[DisplayUtil] ApplyDisplayChanges: SUCCESS");
            return true;
        }
        Logger.Error($"[DisplayUtil] ApplyDisplayChanges: FAILED with code {ret}");
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi, EntryPoint = "ChangeDisplaySettingsExA")]
    private static extern int ChangeDisplaySettingsExNull(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    static bool IsLikelyVirtualByStrings(string deviceString, string deviceId)
    {
        var s = (deviceString ?? "").ToLowerInvariant();
        var id = (deviceId ?? "").ToLowerInvariant();
        string[] keywords = { "virtual", "indirect", "idd", "headless" };
        
        // DEBUG: Log the actual device ID patterns we see
        if (id.Contains("display"))
        {
            Logger.Debug($"[DEBUG] Virtual monitor detection - DeviceString: '{deviceString}', DeviceID: '{deviceId}'");
        }
        
        // Simple and reliable: Any high-numbered display (> 10) is very likely virtual
        // Extract display number from device name: \\.\DISPLAY22 -> 22
        var displayNumMatch = System.Text.RegularExpressions.Regex.Match(deviceString ?? "", @"DISPLAY(\d+)");
        if (displayNumMatch.Success && int.TryParse(displayNumMatch.Groups[1].Value, out int displayNum))
        {
            if (displayNum > 10)
            {
                Logger.Debug($"[DEBUG] Virtual monitor detected via display number {displayNum}: {deviceString}");
                return true;
            }
        }
        
        return keywords.Any(k => s.Contains(k) || id.Contains(k));
    }

    static bool HasNoPhysicalMonitors(IntPtr hmon)
    {
        try { return GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out var n) && n == 0; }
        catch { return false; }
    }

    /// Trả về true nếu \\.\DISPLAYx trông giống màn hình ảo (chuỗi nhận diện + không có physical monitor).
    public static bool IsVirtualDisplay(string displayName, IntPtr hmon)
    {
        // Primary method: compare against pre-VDD physical monitor snapshot
        if (_knownPhysicalNames.Count > 0)
            return displayName == null || !_knownPhysicalNames.Contains(displayName);

        // Fallback (no snapshot yet): use adapter string detection + physical monitor count
        for (uint devNum = 0; ; devNum++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, devNum, ref dd, 0)) break;
            if (!string.Equals(dd.DeviceName, displayName, StringComparison.OrdinalIgnoreCase)) continue;

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0))
            {
                if (IsLikelyVirtualByStrings(mon.DeviceString, mon.DeviceID))
                    return true;
            }
            return HasNoPhysicalMonitors(hmon);
        }
        return HasNoPhysicalMonitors(hmon);
    }

    // Known physical display names from snapshot (set by VirtualDisplayManager)
    private static readonly HashSet<string> _knownPhysicalNames = new(StringComparer.OrdinalIgnoreCase);
    public static void SetKnownPhysicalNames(IEnumerable<string> names)
    {
        _knownPhysicalNames.Clear();
        foreach (var n in names) _knownPhysicalNames.Add(n);
    }

    /// Kiểm tra một \\.\DISPLAYx có phải Primary không
    public static bool IsPrimary(string deviceName)
    {
        for (uint devNum = 0; ; devNum++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, devNum, ref dd, 0)) break;
            if (!string.Equals(dd.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            return (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
        }
        return deviceName.Equals(@"\\.\DISPLAY1", StringComparison.OrdinalIgnoreCase);
    }

    /// Kiểm tra một \\.\DISPLAYx có đang active (attached) không
    public static bool IsDisplayActive(string deviceName)
    {
        for (uint devNum = 0; ; devNum++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, devNum, ref dd, 0)) break;
            if (!string.Equals(dd.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            return (dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0;
        }
        return false;
    }

    // --- CCD (Connecting and Configuring Displays) API ---
    const uint QDC_ALL_PATHS = 0x00000001;
    const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    const uint SDC_APPLY = 0x00000080;
    const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    const uint SDC_ALLOW_CHANGES = 0x00000400;
    const uint SDC_TOPOLOGY_SUPPLIED = 0x00000004;
    const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;
    const int ERROR_SUCCESS = 0;

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPathArrayElements,
        DISPLAYCONFIG_PATH_INFO[]? pathArray, uint numModeInfoArrayElements,
        DISPLAYCONFIG_MODE_INFO[]? modeInfoArray, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
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
    struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType; // DISPLAYCONFIG_MODE_INFO_TYPE: 1=SOURCE, 2=TARGET
        public uint id;
        public LUID adapterId;
        // Union: TARGET_MODE(48B) / SOURCE_MODE(20B) / DESKTOP_IMAGE_INFO(40B)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] modeData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type; // DISPLAYCONFIG_DEVICE_INFO_TYPE
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    /// <summary>
    /// Set display positions using CCD API (reliable for IDD/VDD virtual monitors).
    /// ChangeDisplaySettingsEx doesn't reliably move IDD virtual displays — CCD is required.
    /// </summary>
    /// <param name="positions">Map of device name → (x, y) position</param>
    public static bool SetMonitorPositionsViaCCD(Dictionary<string, (int x, int y)> positions)
    {
        try
        {
            // Query current active config
            int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (err != ERROR_SUCCESS)
            {
                Logger.Error($"[CCD Position] GetDisplayConfigBufferSizes failed: {err}");
                return false;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err != ERROR_SUCCESS)
            {
                Logger.Error($"[CCD Position] QueryDisplayConfig failed: {err}");
                return false;
            }

            // Build path→deviceName mapping
            var pathDeviceNames = new string[pathCount];
            for (int i = 0; i < pathCount; i++)
            {
                var deviceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                deviceName.header.type = 1; // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                deviceName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                deviceName.header.adapterId = paths[i].sourceInfo.adapterId;
                deviceName.header.id = paths[i].sourceInfo.id;

                err = DisplayConfigGetDeviceInfo(ref deviceName);
                pathDeviceNames[i] = err == ERROR_SUCCESS
                    ? deviceName.viewGdiDeviceName?.TrimEnd('\0') ?? ""
                    : "";
            }

            // Modify source mode positions
            // Source mode layout: uint width(4) + uint height(4) + uint pixelFormat(4) + int posX(4) + int posY(4)
            // Position offset in modeData: byte 12 (posX) and byte 16 (posY)
            int modified = 0;
            for (int i = 0; i < pathCount; i++)
            {
                string gdiName = pathDeviceNames[i];
                if (!positions.TryGetValue(gdiName, out var pos))
                    continue;

                uint sourceModeIdx = paths[i].sourceInfo.modeInfoIdx;
                if (sourceModeIdx >= modeCount || sourceModeIdx == 0xFFFFFFFF)
                    continue;

                // infoType == 1 means SOURCE mode
                if (modes[sourceModeIdx].infoType != 1)
                {
                    Logger.Warn($"[CCD Position] Mode[{sourceModeIdx}] for {gdiName} is not SOURCE mode (type={modes[sourceModeIdx].infoType})");
                    continue;
                }

                // Read current position for logging
                int oldX = BitConverter.ToInt32(modes[sourceModeIdx].modeData, 12);
                int oldY = BitConverter.ToInt32(modes[sourceModeIdx].modeData, 16);

                // Write new position
                byte[] xBytes = BitConverter.GetBytes(pos.x);
                byte[] yBytes = BitConverter.GetBytes(pos.y);
                Buffer.BlockCopy(xBytes, 0, modes[sourceModeIdx].modeData, 12, 4);
                Buffer.BlockCopy(yBytes, 0, modes[sourceModeIdx].modeData, 16, 4);

                Logger.Info($"[CCD Position] {gdiName}: ({oldX},{oldY}) → ({pos.x},{pos.y})");
                modified++;
            }

            if (modified == 0)
            {
                Logger.Warn("[CCD Position] No positions modified");
                return false;
            }

            // Apply modified config
            err = SetDisplayConfig(
                pathCount, paths,
                modeCount, modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);

            if (err != ERROR_SUCCESS)
            {
                Logger.Error($"[CCD Position] SetDisplayConfig failed: {err}");
                return false;
            }

            Logger.Info($"[CCD Position] SetDisplayConfig applied successfully ({modified} monitors repositioned)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[CCD Position] Exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Set Windows display topology to "Show Only" on the target device.
    /// Uses CCD API (SetDisplayConfig) for reliable topology changes.
    /// Falls back to ChangeDisplaySettingsEx if CCD fails.
    /// </summary>
    /// <param name="targetDeviceName">The \\.\DISPLAYx to keep active</param>
    /// <returns>True if topology was applied successfully</returns>
    public static bool SetTopologyShowOnly(string targetDeviceName)
    {
        Logger.Info($"[DisplayUtil] SetTopologyShowOnly: target={targetDeviceName}");

        // Enumerate all active displays
        var activeDisplays = new List<string>();
        for (uint devNum = 0; ; devNum++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, devNum, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                activeDisplays.Add(dd.DeviceName);
        }
        Logger.Info($"[DisplayUtil] Active displays: {string.Join(", ", activeDisplays)}");

        // Try CCD API first (reliable for topology changes)
        if (SetTopologyShowOnlyViaCCD(targetDeviceName))
        {
            Logger.Info("[DisplayUtil] SetTopologyShowOnly: CCD API succeeded");
            return true;
        }

        Logger.Warn("[DisplayUtil] CCD API failed, falling back to ChangeDisplaySettingsEx...");
        return SetTopologyShowOnlyViaLegacy(targetDeviceName, activeDisplays);
    }

    /// <summary>
    /// Use CCD API (QueryDisplayConfig + SetDisplayConfig) to show only the target display.
    /// This properly updates DXGI output topology.
    /// </summary>
    static bool SetTopologyShowOnlyViaCCD(string targetDeviceName)
    {
        try
        {
            // Step 1: Query current active display config
            int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (err != ERROR_SUCCESS)
            {
                Logger.Error($"[DisplayUtil] CCD: GetDisplayConfigBufferSizes failed: {err}");
                return false;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err != ERROR_SUCCESS)
            {
                Logger.Error($"[DisplayUtil] CCD: QueryDisplayConfig failed: {err}");
                return false;
            }

            Logger.Info($"[DisplayUtil] CCD: {pathCount} active paths, {modeCount} modes");

            // Step 2: Find which path corresponds to the target display
            int targetPathIndex = -1;
            for (int i = 0; i < pathCount; i++)
            {
                // Get the GDI device name for this source
                var deviceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                deviceName.header.type = 1; // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                deviceName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                deviceName.header.adapterId = paths[i].sourceInfo.adapterId;
                deviceName.header.id = paths[i].sourceInfo.id;

                err = DisplayConfigGetDeviceInfo(ref deviceName);
                if (err == ERROR_SUCCESS)
                {
                    string gdiName = deviceName.viewGdiDeviceName?.TrimEnd('\0') ?? "";
                    Logger.Info($"[DisplayUtil] CCD: Path[{i}] source={gdiName}");

                    if (string.Equals(gdiName, targetDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetPathIndex = i;
                        Logger.Info($"[DisplayUtil] CCD: Target path found at index {i}");
                    }
                }
            }

            if (targetPathIndex < 0)
            {
                Logger.Error($"[DisplayUtil] CCD: Target display {targetDeviceName} not found in active paths!");
                return false;
            }

            // Step 3: Build new config with only the target path
            // Keep only the target path and its associated modes
            var targetPath = paths[targetPathIndex];

            // Collect mode indices used by the target path
            var usedModeIndices = new HashSet<uint>();
            if (targetPath.sourceInfo.modeInfoIdx != 0xFFFFFFFF) // DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                usedModeIndices.Add(targetPath.sourceInfo.modeInfoIdx);
            if (targetPath.targetInfo.modeInfoIdx != 0xFFFFFFFF)
                usedModeIndices.Add(targetPath.targetInfo.modeInfoIdx);

            // Build new mode array with only the used modes, and remap indices
            var newModes = new List<DISPLAYCONFIG_MODE_INFO>();
            var indexMap = new Dictionary<uint, uint>();
            foreach (var oldIdx in usedModeIndices)
            {
                if (oldIdx < modeCount)
                {
                    indexMap[oldIdx] = (uint)newModes.Count;
                    newModes.Add(modes[oldIdx]);
                }
            }

            // Remap mode indices in the target path
            if (indexMap.ContainsKey(targetPath.sourceInfo.modeInfoIdx))
                targetPath.sourceInfo.modeInfoIdx = indexMap[targetPath.sourceInfo.modeInfoIdx];
            if (indexMap.ContainsKey(targetPath.targetInfo.modeInfoIdx))
                targetPath.targetInfo.modeInfoIdx = indexMap[targetPath.targetInfo.modeInfoIdx];

            var newPaths = new DISPLAYCONFIG_PATH_INFO[] { targetPath };
            var newModesArr = newModes.ToArray();

            // Step 4: Apply new topology
            err = SetDisplayConfig(
                (uint)newPaths.Length, newPaths,
                (uint)newModesArr.Length, newModesArr,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);

            if (err != ERROR_SUCCESS)
            {
                Logger.Error($"[DisplayUtil] CCD: SetDisplayConfig failed: {err}");
                return false;
            }

            Logger.Info("[DisplayUtil] CCD: SetDisplayConfig applied successfully");

            // Step 5: Verify via DXGI
            System.Threading.Thread.Sleep(500);
            bool verified = true;
            foreach (var displayName in GetActiveDisplayNames())
            {
                if (!string.Equals(displayName, targetDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[DisplayUtil] CCD: {displayName} still active after SetDisplayConfig!");
                    verified = false;
                }
            }

            if (verified)
                Logger.Info("[DisplayUtil] CCD: verified — only target display is active");

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[DisplayUtil] CCD: Exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get list of currently active display device names.
    /// </summary>
    static List<string> GetActiveDisplayNames()
    {
        var list = new List<string>();
        for (uint devNum = 0; ; devNum++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, devNum, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                list.Add(dd.DeviceName);
        }
        return list;
    }

    /// <summary>
    /// Legacy fallback using ChangeDisplaySettingsEx.
    /// </summary>
    static bool SetTopologyShowOnlyViaLegacy(string targetDeviceName, List<string> activeDisplays)
    {
        const int CDS_NORESET = 0x10000000;
        const int CDS_SET_PRIMARY = 0x00000010;

        // Step 1: Set target as primary at position (0,0)
        {
            var dm = new DEVMODE
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };
            if (EnumDisplaySettingsEx(targetDeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
            {
                dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                dm.dmPositionX = 0;
                dm.dmPositionY = 0;
                int ret = ChangeDisplaySettingsEx(targetDeviceName, ref dm, IntPtr.Zero,
                    CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                Logger.Info($"[DisplayUtil] Set primary {targetDeviceName}: result={ret}");
            }
        }

        // Step 2: Detach all displays that are NOT the target
        foreach (var displayName in activeDisplays)
        {
            if (string.Equals(displayName, targetDeviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            var dm = new DEVMODE
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };

            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
            dm.dmPelsWidth = 0;
            dm.dmPelsHeight = 0;
            dm.dmPositionX = 0;
            dm.dmPositionY = 0;

            int ret = ChangeDisplaySettingsEx(displayName, ref dm, IntPtr.Zero,
                CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            Logger.Info($"[DisplayUtil] Detach {displayName}: result={ret}");
        }

        // Step 3: Apply
        bool success = ApplyDisplayChanges();
        Logger.Info($"[DisplayUtil] SetTopologyShowOnly (legacy): applied={success}");

        // Step 4: Verify
        System.Threading.Thread.Sleep(500);
        foreach (var displayName in activeDisplays)
        {
            if (string.Equals(displayName, targetDeviceName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsDisplayActive(displayName))
                Logger.Warn($"[DisplayUtil] SetTopologyShowOnly: {displayName} STILL active after legacy detach!");
        }

        return success;
    }

    /// <summary>
    /// Restore display topology from saved snapshots.
    /// Re-attaches each display with its saved position/resolution.
    /// </summary>
    /// <param name="snapshots">Saved display mode snapshots from before Show Only was applied</param>
    /// <returns>True if topology was restored successfully</returns>
    public static bool RestoreExtendTopology(List<DisplayModeSnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return false;

        const int CDS_NORESET = 0x10000000;

        Logger.Info($"[DisplayUtil] RestoreExtendTopology: restoring {snapshots.Count} displays");

        foreach (var snap in snapshots)
        {
            var dm = new DEVMODE
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };

            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            dm.dmPositionX = snap.X;
            dm.dmPositionY = snap.Y;
            dm.dmPelsWidth = snap.Width;
            dm.dmPelsHeight = snap.Height;
            dm.dmDisplayFrequency = snap.Frequency;

            int ret = ChangeDisplaySettingsEx(snap.DeviceName, ref dm, IntPtr.Zero,
                CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            Logger.Info($"[DisplayUtil] Restore {snap.DeviceName}: ({snap.X},{snap.Y}) {snap.Width}x{snap.Height}@{snap.Frequency}Hz result={ret}");
        }

        bool success = ApplyDisplayChanges();
        Logger.Info($"[DisplayUtil] RestoreExtendTopology: applied={success}");
        return success;
    }
}

enum ScreenOrientation : int { DMDO_DEFAULT = 0, DMDO_90 = 1, DMDO_180 = 2, DMDO_270 = 3 }
