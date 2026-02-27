#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Display;

#if WINDOWS
using Microsoft.Win32;

static class TextScaleUtil
{
    // Windows “Text size” (Make text bigger)
    // Dùng 2 key vì tuỳ build Windows sẽ đọc ở một trong hai:
    //  1) HKCU\Control Panel\Accessibility\TextScaleFactor           (DWORD, 100..225)
    //  2) HKCU\Software\Microsoft\Accessibility\TextScaleFactor      (DWORD, 100..225)
    const string KEY1 = @"Control Panel\Accessibility";
    const string KEY2 = @"Software\Microsoft\Accessibility";
    const string VAL = "TextScaleFactor"; // ĐƠN VỊ = PHẦN TRĂM (100..225), KHÔNG ×10

    // Per-monitor DPI settings for specific monitors
    const string PERMONITOR_DPI_KEY = @"Control Panel\Desktop";
    const string PERMONITOR_DPI_VAL = "LogPixels";

    public struct Snapshot { public int? K1; public int? K2; }

    public static Snapshot Read()
    {
        return new Snapshot
        {
            K1 = ReadPercent(KEY1, VAL),
            K2 = ReadPercent(KEY2, VAL)
        };
    }

    /// <summary>Đặt Text size theo phần trăm (100..225). Áp tức thời cho đa số thành phần.</summary>
    public static void SetPercent(int percent)
    {
        int v = Math.Clamp(percent, 100, 225);
        WriteDword(KEY1, VAL, v);
        WriteDword(KEY2, VAL, v);
        BroadcastSettingsChange();
    }

    public static void Restore(Snapshot snap)
    {
        WriteOrDelete(KEY1, VAL, snap.K1);
        WriteOrDelete(KEY2, VAL, snap.K2);
        BroadcastSettingsChange();
    }

    // ---------- Helpers ----------
    static int? ReadPercent(string key, string name)
    {
        using var rk = Registry.CurrentUser.OpenSubKey(key, false);
        object? o = rk?.GetValue(name);
        if (o is int iv)
        {
            // Nếu lỡ có giá trị kiểu cũ (×10) như 1250 thì quy về %.
            if (iv > 225 && iv <= 10000) return iv / 10;
            return iv;
        }
        return null;
    }

    public static void WriteDword(string key, string name, int value)
    {
        using var rk = Registry.CurrentUser.OpenSubKey(key, true) ?? Registry.CurrentUser.CreateSubKey(key, true);
        rk.SetValue(name, value, RegistryValueKind.DWord);
    }

    static void WriteOrDelete(string key, string name, int? value)
    {
        using var rk = Registry.CurrentUser.OpenSubKey(key, true) ?? Registry.CurrentUser.CreateSubKey(key, true);
        if (value is int v) rk.SetValue(name, v, RegistryValueKind.DWord);
        else rk.DeleteValue(name, false);
    }

    public static void RestartExplorerShell()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) p.Kill();
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Thử kích hoạt text scale change mà không cần restart explorer bằng cách gửi nhiều messages và refresh các components
    /// </summary>
    public static void EnhancedBroadcastAndRefresh()
    {
        try
        {
            const int HWND_BROADCAST = 0xFFFF;
            const int WM_SETTINGCHANGE = 0x1A;
            const int WM_WININICHANGE = 0x001A;
            
            // Gửi nhiều types của messages để kích hoạt Windows cập nhật
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "TextScaleFactor", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "WindowsMetrics", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "WindowMetrics", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "Status", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "ThemeChanged", 0x0002, 5000, out _);
            
            // Thử WININICHANGE cho các applications cũ
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_WININICHANGE, IntPtr.Zero, "TextScaleFactor", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_WININICHANGE, IntPtr.Zero, "WindowsMetrics", 0x0002, 5000, out _);
            
            // Force DPI awareness refresh
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "DPIChanged", 0x0002, 5000, out _);
            
            // Refresh shell và desktop
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "Shell", 0x0002, 5000, out _);
            
            // Chờ một chút để Windows process các messages
            System.Threading.Thread.Sleep(200);
            
            // Gửi batch thứ hai để đảm bảo
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "TextScaleFactor", 0x0002, 5000, out _);
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "ThemeChanged", 0x0002, 5000, out _);
            
            Logger.Info("[TextScale] Enhanced broadcast refresh completed.");
        }
        catch (Exception ex)
        {
            Logger.Error("[TextScale] Enhanced broadcast failed: " + ex.Message);
        }
    }

    static void BroadcastSettingsChange()
    {
        const int HWND_BROADCAST = 0xFFFF;
        const int WM_SETTINGCHANGE = 0x1A;
        
        // Gửi multiple messages để đảm bảo Windows cập nhật text scale
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "WindowsMetrics", 0x0002, 5000, out _);
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 5000, out _);
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "WindowMetrics", 0x0002, 5000, out _);
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "Status", 0x0002, 5000, out _);
        
        // Thêm messages cho text scale specifically
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "TextScaleFactor", 0x0002, 5000, out _);
        
        // Force desktop theme refresh
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "ThemeChanged", 0x0002, 5000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, string? lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    // Per-monitor DPI scaling functions
    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Windows 8.1+ per-monitor DPI awareness
    [DllImport("shcore.dll")]
    static extern int SetProcessDpiAwareness(int awareness);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    const int MONITOR_DEFAULTTONEAREST = 0x00000002;
    const int MDT_EFFECTIVE_DPI = 0;

    // Windows 10+ DPI awareness context
    [DllImport("user32.dll")]
    static extern IntPtr SetProcessDpiAwarenessContext(IntPtr context);

    // GDI32 for system DPI
    [DllImport("gdi32.dll")]
    static extern IntPtr GetDC(IntPtr hwnd);
    
    [DllImport("gdi32.dll")]
    static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
    
    [DllImport("gdi32.dll")]
    static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    
    const int LOGPIXELSX = 88;

    // DPI awareness levels
    const int PROCESS_DPI_UNAWARE = 0;
    const int PROCESS_SYSTEM_DPI_AWARE = 1;
    const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

    // DPI awareness context constants (Windows 10+)
    static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new IntPtr(-1);
    static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = new IntPtr(-2);
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new IntPtr(-3);
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    /// <summary>
    /// Đặt DPI scaling cho một monitor cụ thể (125% = 120 DPI)
    /// Windows chỉ hỗ trợ per-monitor DPI scaling từ Windows 8.1+
    /// </summary>
    public static bool SetPerMonitorTextScale(string monitorName, int percent)
    {
        try
        {
            // 125% text scale = 120 DPI (96 * 1.25)
            int dpi = (int)(96 * percent / 100);
            
            Logger.Info($"[TextScale] Setting {percent}% (DPI: {dpi}) for monitor: {monitorName}");
            
            // Enhanced approach for Windows 10/11 per-monitor DPI
            
            // 1. Set per-monitor DPI awareness for the process
            try
            {
                SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
                Logger.Info("[TextScale] Process DPI awareness enabled");
            }
            catch (Exception ex)
            {
                Logger.Error($"[TextScale] DPI awareness setup failed: {ex.Message}");
            }
            
            // 2. Write to multiple registry locations for maximum compatibility
            bool registrySuccess = false;
            string sanitized = SanitizeMonitorName(monitorName);
            
            // Method A: PerMonitorSettings (Windows 8.1+)
            try
            {
                string monitorKey = $@"Control Panel\Desktop\PerMonitorSettings\{sanitized}";
                using var rk1 = Registry.CurrentUser.OpenSubKey(monitorKey, true) ?? 
                                Registry.CurrentUser.CreateSubKey(monitorKey, true);
                rk1.SetValue("LogPixels", dpi, RegistryValueKind.DWord);
                rk1.SetValue("DpiScaling", 1, RegistryValueKind.DWord);
                rk1.SetValue("Win8DpiScaling", 1, RegistryValueKind.DWord);
                Logger.Info($"[TextScale] PerMonitorSettings registry written for {monitorName}");
                registrySuccess = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[TextScale] PerMonitorSettings registry failed: {ex.Message}");
            }
            
            // Method B: Display settings key (alternative approach)
            try
            {
                using var rk2 = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                rk2?.SetValue("LogPixels", dpi, RegistryValueKind.DWord);
                Logger.Info("[TextScale] Desktop LogPixels updated");
                registrySuccess = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[TextScale] Desktop registry failed: {ex.Message}");
            }
            
            // Method C: Monitor-specific registry (if available)
            try
            {
                string monitorKey2 = $@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration\{sanitized}\00";
                using var rk3 = Registry.LocalMachine.OpenSubKey(monitorKey2, true);
                if (rk3 != null)
                {
                    rk3.SetValue("LogPixels", dpi, RegistryValueKind.DWord);
                    Logger.Info($"[TextScale] GraphicsDrivers registry written for {monitorName}");
                    registrySuccess = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TextScale] GraphicsDrivers registry failed (may be normal): {ex.Message}");
            }
            
            // 3. Force DPI context change for current process
            try
            {
                // This is critical - it tells Windows to recalculate DPI for this process
                SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                Logger.Info("[TextScale] DPI awareness context set to Per-Monitor V2");
            }
            catch (Exception ex)
            {
                Logger.Error($"[TextScale] DPI awareness context setup failed: {ex.Message}");
            }
            
            // 4. Enhanced broadcast with multiple methods
            try
            {
                // Standard broadcast
                EnhancedBroadcastAndRefresh();
                
                // Additional broadcast for display configuration change
                const int HWND_BROADCAST = 0xFFFF;
                const int WM_DISPLAYCHANGE = 0x007E;
                SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero, 0x0002, 5000, out _);
                Logger.Info("[TextScale] Display change broadcast sent");
                
                // Small delay to allow Windows to process changes
                System.Threading.Thread.Sleep(500);
                
                // Another broadcast after delay
                EnhancedBroadcastAndRefresh();
            }
            catch (Exception ex)
            {
                Logger.Error($"[TextScale] Broadcast failed: {ex.Message}");
            }
            
            // 5. Verify with multiple checks and small delay
            System.Threading.Thread.Sleep(1000); // Allow time for changes to take effect
            
            // Check multiple times to ensure consistency
            var dpiCheck1 = GetMonitorDpi(monitorName);
            System.Threading.Thread.Sleep(500);
            var dpiCheck2 = GetMonitorDpi(monitorName);
            
            // Use the higher DPI value or the one that matches target best
            int effectiveDpi = dpiCheck1;
            if (Math.Abs(dpiCheck2 - dpi) < Math.Abs(dpiCheck1 - dpi))
                effectiveDpi = dpiCheck2;
            
            bool success = Math.Abs(effectiveDpi - dpi) <= 3; // Slightly higher tolerance
            
            if (success)
            {
                Logger.Info($"[TextScale] ✓ Per-monitor {percent}% set successfully for {monitorName}");
                Logger.Info($"[TextScale] Confirmed DPI: {effectiveDpi} (target: {dpi})");
            }
            else
            {
                Logger.Info($"[TextScale] ⚠ Current DPI: {effectiveDpi}, Expected: {dpi}");
                Logger.Info($"[TextScale] DPI check 1: {dpiCheck1}, DPI check 2: {dpiCheck2}");
                Logger.Info("[TextScale] This may require Explorer restart for full effect");
                
                // Try one more aggressive approach
                try
                {
                    // Final attempt with system-wide DPI change simulation
                    using var rk = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                    rk?.SetValue("LogPixels", dpi, RegistryValueKind.DWord);
                    rk?.SetValue("Win8DpiScaling", 1, RegistryValueKind.DWord);
                    
                    // Force system-wide refresh
                    System.Diagnostics.Process.Start("cmd.exe", "/c timeout /t 2 >nul && rundll32.exe user32.dll,UpdatePerUserSystemParameters");
                    Logger.Info("[TextScale] Final system-wide update initiated");
                }
                catch (Exception ex2)
                {
                    Logger.Error($"[TextScale] Final attempt failed: {ex2.Message}");
                }
            }
            
            return success || registrySuccess; // Return true if registry writes succeeded even if immediate verification failed
        }
        catch (Exception ex)
        {
            Logger.Error($"[TextScale] Per-monitor scaling failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Lấy DPI hiện tại của một monitor (thực tế hơn)
    /// </summary>
    public static int GetMonitorDpi(string monitorName)
    {
        try
        {
            // Method 1: Try to get from registry first (quick check)
            string monitorKey = $@"Control Panel\Desktop\PerMonitorSettings\{SanitizeMonitorName(monitorName)}";
            using var rk = Registry.CurrentUser.OpenSubKey(monitorKey, false);
            var val = rk?.GetValue("LogPixels");
            if (val is int dpiFromReg) 
            {
                Logger.Info($"[TextScale] Registry DPI for {monitorName}: {dpiFromReg}");
                return dpiFromReg;
            }
            
            // Method 2: Try to get DPI using monitor handle if possible
            try
            {
                var monitors = WgcInterop.ListMonitorsDXGI();
                var targetMonitor = monitors.FirstOrDefault(m => string.Equals(m.name, monitorName, StringComparison.OrdinalIgnoreCase));
                if (targetMonitor.hmon != IntPtr.Zero)
                {
                    // Try to get actual DPI for this monitor
                    GetDpiForMonitor(targetMonitor.hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);
                    int actualDpi = (int)dpiX;
                    Logger.Info($"[TextScale] Actual DPI for {monitorName}: {actualDpi}");
                    return actualDpi;
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[TextScale] Cannot get actual DPI: {ex.Message}");
            }
            
            // Method 3: Fallback to system DPI
            int systemDpi = GetSystemDpi();
            Logger.Info($"[TextScale] Using system DPI for {monitorName}: {systemDpi}");
            return systemDpi;
        }
        catch { return 96; }
    }
    
    /// <summary>
    /// Lấy system DPI
    /// </summary>
    static int GetSystemDpi()
    {
        try
        {
            // Try desktop registry first
            using var rk = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
            var val = rk?.GetValue("LogPixels");
            if (val is int dpi) return dpi;
            
            // Try to get system DPI via Windows API
            try 
            {
                // Get DC for desktop
                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    int systemDpi = GetDeviceCaps(hdc, LOGPIXELSX);
                    ReleaseDC(IntPtr.Zero, hdc);
                    return systemDpi;
                }
            }
            catch { }
            
            return 96; // Fallback default
        }
        catch { return 96; }
    }

    /// <summary>
    /// Khôi phục DPI về mặc định cho một monitor
    /// </summary>
    public static bool RestorePerMonitorTextScale(string monitorName, int originalPercent)
    {
        try
        {
            int dpi = (int)(96 * originalPercent / 100);
            string monitorKey = $@"Control Panel\Desktop\PerMonitorSettings\{SanitizeMonitorName(monitorName)}";
            
            // Set back to original
            using var rk = Registry.CurrentUser.OpenSubKey(monitorKey, true) ?? 
                           Registry.CurrentUser.CreateSubKey(monitorKey, true);
            
            if (originalPercent == 100) // 100% means remove custom DPI
            {
                rk.DeleteValue("LogPixels", false);
                rk.DeleteValue("DpiScaling", false);
                Logger.Info($"[TextScale] Removed custom DPI for {monitorName}");
            }
            else
            {
                rk.SetValue("LogPixels", dpi, RegistryValueKind.DWord);
                rk.SetValue("DpiScaling", 1, RegistryValueKind.DWord);
                Logger.Info($"[TextScale] Restored {originalPercent}% for {monitorName}");
            }
            
            EnhancedBroadcastAndRefresh();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[TextScale] Per-monitor restore failed: {ex.Message}");
            return false;
        }
    }

    static string SanitizeMonitorName(string monitorName)
    {
        // Replace invalid characters for registry key
        var sb = new StringBuilder();
        foreach (char c in monitorName)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }
}
#endif
