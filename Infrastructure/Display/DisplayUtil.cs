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
            return false;

        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmDisplayFrequency = hz;

        int ret = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_GLOBAL, IntPtr.Zero);
        return ret == DISP_CHANGE_SUCCESSFUL;
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
}

enum ScreenOrientation : int { DMDO_DEFAULT = 0, DMDO_90 = 1, DMDO_180 = 2, DMDO_270 = 3 }
