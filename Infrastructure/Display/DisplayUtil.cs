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
        // Bắt cặp DISPLAYx -> adapter
        for (uint devNum = 0; ; devNum++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, devNum, ref dd, 0)) break;
            if (!string.Equals(dd.DeviceName, displayName, StringComparison.OrdinalIgnoreCase)) continue;

            // Thiết bị con
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

    /// <summary>
    /// Set Windows display topology to "Show Only" on the target device.
    /// Detaches all other active displays and makes target the sole primary at (0,0).
    /// </summary>
    /// <param name="targetDeviceName">The \\.\DISPLAYx to keep active</param>
    /// <returns>True if topology was applied successfully</returns>
    public static bool SetTopologyShowOnly(string targetDeviceName)
    {
        const int CDS_NORESET = 0x10000000;
        const int CDS_SET_PRIMARY = 0x00000010;

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

        // Step 1: Set target as primary at position (0,0) FIRST
        // Must be done before detaching others, otherwise Windows has no primary.
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

        // Step 3: Apply all changes
        bool success = ApplyDisplayChanges();
        Logger.Info($"[DisplayUtil] SetTopologyShowOnly: applied={success}");
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
