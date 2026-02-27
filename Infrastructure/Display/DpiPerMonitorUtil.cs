#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace RemotePlayServer.Infrastructure.Display;

#if WINDOWS
using Microsoft.Win32;

static class DpiPerMonitorUtil
{
    // HKCU\Control Panel\Desktop\PerMonitorSettings\<SubKeyForEachMonitor>
    private const string PER_MONITOR_KEY = @"Control Panel\Desktop\PerMonitorSettings";

    public struct PerMonDpi
    {
        public string SubKey { get; set; }     // tên key con (đại diện 1 màn)
        public int? DpiValue { get; set; }     // DWORD, thường = 96(100%), 120(125%), 144(150%)...
    }

    public static List<PerMonDpi> SnapshotAll()
    {
        var list = new List<PerMonDpi>();
        using var root = Registry.CurrentUser.OpenSubKey(PER_MONITOR_KEY, writable: false);
        if (root == null) return list;

        foreach (var sub in root.GetSubKeyNames())
        {
            try
            {
                using var k = root.OpenSubKey(sub, writable: false);
                object? v = k?.GetValue("DpiValue");
                int? dpi = v is int iv ? iv : (v as int?);
                list.Add(new PerMonDpi { SubKey = sub, DpiValue = dpi });
            }
            catch { /* skip */ }
        }
        return list;
    }

    public static void SaveSnapshot(string path, List<PerMonDpi> snaps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snaps));
    }

    public static List<PerMonDpi>? LoadSnapshot(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<PerMonDpi>>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    /// <summary>Đặt Scale % cho TẤT CẢ subkey hiện có (màn hình hiện diện).</summary>
    public static void SetAllMonitorsScalePercent(int percent)
    {
        int logPixels = (int)Math.Round(96 * (percent / 100.0)); // 100%=96, 125%=120, 150%=144...
        using var root = Registry.CurrentUser.OpenSubKey(PER_MONITOR_KEY, writable: true) ?? Registry.CurrentUser.CreateSubKey(PER_MONITOR_KEY, true);
        foreach (var sub in root.GetSubKeyNames())
        {
            try
            {
                using var k = root.OpenSubKey(sub, writable: true) ?? root.CreateSubKey(sub, true);
                k.SetValue("DpiValue", logPixels, RegistryValueKind.DWord);
            }
            catch { /* best-effort */ }
        }

        // phát tín hiệu cập nhật
        BroadcastForSettingsChange();
    }

    public static void Restore(List<PerMonDpi> snaps)
    {
        using var root = Registry.CurrentUser.OpenSubKey(PER_MONITOR_KEY, writable: true) ?? Registry.CurrentUser.CreateSubKey(PER_MONITOR_KEY, true);
        foreach (var s in snaps)
        {
            try
            {
                using var k = root.OpenSubKey(s.SubKey, writable: true) ?? root.CreateSubKey(s.SubKey, true);
                if (s.DpiValue is int v) k.SetValue("DpiValue", v, RegistryValueKind.DWord);
                else k.DeleteValue("DpiValue", false);
            }
            catch { /* best-effort */ }
        }

        BroadcastForSettingsChange();
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    const uint WM_SETTINGCHANGE = 0x001A;
    const uint HWND_BROADCAST = 0xFFFF;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    static void BroadcastForSettingsChange()
    {
        try
        {
            IntPtr result;
            SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "WindowMetrics", SMTO_ABORTIFHUNG, 5000, out result);
        }
        catch { /* ignore */ }
    }
}
#endif
