#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Web;
using System.Runtime.InteropServices;
using System.IO;
using System.Xml.Linq;
using Microsoft.Win32;
using System.Diagnostics;

partial class Program
{
    static async Task Main()
    {
        // Khôi phục nếu phiên trước bị dừng đột ngột (marker còn tồn tại)
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--restore-if-needed", StringComparison.OrdinalIgnoreCase)))
        {
            DisplayGuard.RestoreIfNeededOnStartup();
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== RemotePlayServer (with startup steps 1→6) ===");

        // Chụp trạng thái ban đầu và tạo marker phiên
        DisplayGuard.CaptureSnapshotAtStartup();
        Console.WriteLine("[Setup] Step 1/5 completed - State captured.");
        Thread.Sleep(1000); // Allow system to stabilize

        // ---------------- STEP 1 ----------------
        Console.WriteLine("[Setup] Step 2/5: Configuring multi-monitor taskbar...");
        StartupSteps.DisableMultiMonitorTaskbar(); // KHÔNG restart explorer
        Console.WriteLine("[Setup] Taskbar settings updated.");
        Thread.Sleep(1500); // Allow taskbar settings to settle

        // ---------------- STEP 2 ----------------
        Console.WriteLine("[Setup] Step 3/5: Configuring Virtual Display Driver...");
        StartupSteps.EnsureVddResolutionThenToggleDriver(); // C:\VirtualDisplayDriver\vdd_settings.xml
        Console.WriteLine("[Setup] VDD configuration completed.");
        Thread.Sleep(2000); // Let display driver settle

        // Liệt kê monitor sau khi toggle/taskbar
        var monitors = WgcInterop.ListMonitorsDXGI();
        Console.WriteLine("=== Monitors ===");
        for (int i = 0; i < monitors.Count; i++)
            Console.WriteLine($"{i,3}: {monitors[i].name}  {monitors[i].width}x{monitors[i].height}");

        // ---------------- STEP 3 ----------------
        Console.WriteLine("[Setup] Step 4/5: Extending desktop with virtual monitor...");
        StartupSteps.EnsureExtendDesktopWithVirtual();
        Console.WriteLine("[Setup] Desktop extension completed.");
        Thread.Sleep(2000); // Allow display topology to settle

        // ---------------- STEP 4 ----------------
        Console.WriteLine("[Setup] Step 5/5: Applying global 125% text scale...");
        // Set text scale 125% - always use global for multi-monitor setup
        StartupSteps.SetTextScale125_Global();
        Console.WriteLine("[Setup] Text scale configuration completed.");
        Thread.Sleep(1000); // Allow text scale to settle
        
        Console.WriteLine("[Setup] ✅ All configuration steps completed successfully!");

        // ---------------- STEP 5 ----------------
        var tiles = StartupSteps.GetSixTiles_1360x765_with_1px_gutter();
        Console.WriteLine("=== Six tiles (ID 0..5, 1360x765, sep=1) ===");
        for (int i = 0; i < tiles.Count; i++)
            Console.WriteLine($"{i}: x={tiles[i].x} y={tiles[i].y} w={tiles[i].w} h={tiles[i].h}");

        // Chuẩn bị server
        var windows = Win32.ListTopLevelWindows()
            .Where(w => !string.IsNullOrWhiteSpace(w.title))
            .Where(w => WgcInterop.IsCapturableWindow(w.hwnd))
            .ToList();
        for (int i = 0; i < windows.Count; i++) Console.WriteLine($"{i,3}: {windows[i].title}");
        if (windows.Count == 0) Console.WriteLine("(!) Không tìm thấy cửa sổ.");

        monitors = WgcInterop.ListMonitorsDXGI(); // refresh lần nữa
        int port = 8288;
        var server = new SignalAndRestServer($"http://+:{port}/");
        server.SetWindows(Win32.ListTopLevelWindows()
            .Where(w => !string.IsNullOrWhiteSpace(w.title))
            .Where(w => WgcInterop.IsCapturableWindow(w.hwnd))
            .ToList());
        server.SetMonitors(monitors.Select(m => (m.hmon, m.name, m.width, m.height)).ToList());
        InputInjector.OnLog = s => Console.WriteLine($"[INJECT] {DateTime.Now:HH:mm:ss.fff} {s}");

        await server.StartAsync();
        Console.WriteLine($"   • http://localhost:{port}/api/layout");
        foreach (var ip in NetUtil.GetLocalIPv4Addresses())
            Console.WriteLine($"   • ws://{ip}:{port}/signal?mid=<id>   hoặc   ws://{ip}:{port}/signal?wid=<id>");
        Console.WriteLine($"   • http://localhost:{port}/api/windows");
        Console.WriteLine($"   • http://localhost:{port}/api/monitors");
        Console.WriteLine($"   • http://localhost:{port}/api/cluster");
        Console.WriteLine("Server is running. Press ENTER to exit.");
        Console.ReadLine();

        // Graceful shutdown with timeout protection
        try
        {
            Console.WriteLine("[Shutdown] Stopping server...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StopAsync();
            Console.WriteLine("[Shutdown] Server stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Shutdown] Server stop error: {ex.Message}");
        }

        // Khôi phục lại trạng thái ban đầu với timeout protection
        try
        {
            Console.WriteLine("[Shutdown] Restoring display settings...");
            DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(15));
            Console.WriteLine("[Shutdown] Display restore completed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Shutdown] Display restore failed: {ex.Message}");
            Console.WriteLine("[Shutdown] You may need to manually restart or restore display settings.");
        }

        // Force cleanup any remaining resources
        try
        {
            Console.WriteLine("[Shutdown] Force cleanup...");
            await SignalAndRestServer.ForceCleanupResources();
        
        Console.WriteLine();
        Console.WriteLine("=== Optional Actions ===");
        Console.WriteLine("If you notice text size didn't change properly:");
        Console.WriteLine("  1. Restart Explorer manually:");
        Console.WriteLine("     • Ctrl+Shift+Right Click on Start -> Restart Explorer");
        Console.WriteLine("     • Or: taskkill /f /im explorer.exe && start explorer.exe");
        Console.WriteLine("  2. Or run with --restore-if-needed flag if startup was interrupted");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Shutdown] Force cleanup failed: {ex.Message}");
        }

        Console.WriteLine("[Shutdown] Server exited.");
    }
}

// ====================== StartupSteps: 1→6 ======================
static class StartupSteps
{
    const string DRIVER_NAME = "Virtual Display Driver";
    const string MONITOR_NAME = "Virtual Desktop Monitor";

    // (1) Bổ sung 4082×1532@30 vào C:\VirtualDisplayDriver\vdd_settings.xml + enable adapter & monitor
    public static void EnsureVddResolutionThenToggleDriver(
        string settingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml")
    {
        try
        {
            // Đảm bảo 4082x1532@30
            EnsureResolutionInVddXml(settingsPath, 4082, 1532, 30);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[VDD] XML edit failed: " + ex.Message);
        }

        try
        {
            // Ưu tiên enable ADAPTER dưới "Display adapters"
            var adapterId = FindDeviceInstanceIdByNameAndClass(DRIVER_NAME, "Display adapters");
            if (string.IsNullOrWhiteSpace(adapterId))
            {
                // fallback: tìm theo tên bất kể class
                adapterId = FindDeviceInstanceIdByNameAndClass(DRIVER_NAME, null);
            }

            if (!string.IsNullOrWhiteSpace(adapterId))
            {
                RunPnputil("/scan-devices"); Thread.Sleep(500);
                bool isDisabled = IsDeviceDisabled(adapterId);
                if (isDisabled)
                {
                    RunPnputil($"/enable-device \"{adapterId}\"");
                }
                else
                {
                    RunPnputil($"/disable-device \"{adapterId}\"");
                    RunPnputil($"/enable-device \"{adapterId}\"");
                }
                RunPnputil("/scan-devices");
            }
            else
            {
                Console.WriteLine("[VDD] Adapter not found. Check if the driver is installed under Display adapters.");
            }

            // Sau khi bật adapter, thử enable luôn MONITOR (dưới "Monitors") nếu có
            var monitorId = FindDeviceInstanceIdByNameAndClass(MONITOR_NAME, "Monitors");
            if (!string.IsNullOrWhiteSpace(monitorId) && IsDeviceDisabled(monitorId))
            {
                RunPnputil($"/enable-device \"{monitorId}\"");
                RunPnputil("/scan-devices");
            }

            // Ép Windows apply topology: extend
            TryExtendDesktop();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[VDD] Toggle/enable via pnputil failed: " + ex.Message);
            Console.WriteLine("      Hãy Enable thủ công trong Device Manager nếu cần.");
        }
    }

    static void EnsureResolutionInVddXml(string path, int w, int h, int hz)
    {
        if (!File.Exists(path)) { Console.WriteLine("[VDD] File not found: " + path); return; }
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? new XElement("vdd_settings");
        var resRoot = root.Element("resolutions") ?? new XElement("resolutions");
        if (root.Element("resolutions") == null) root.Add(resRoot);

        bool exists = resRoot.Elements("resolution")
            .Any(r => (int?)r.Element("width") == w && (int?)r.Element("height") == h && (int?)r.Element("refresh_rate") == hz);
        if (!exists)
        {
            resRoot.Add(new XElement("resolution",
                new XElement("width", w),
                new XElement("height", h),
                new XElement("refresh_rate", hz)));
            doc.Save(path);
            Console.WriteLine($"[VDD] Added resolution {w}x{h}@{hz} to {path}");
        }
        else Console.WriteLine("[VDD] Resolution already present.");
    }

    // ---- Device helpers ----
    static string FindDeviceInstanceIdByNameAndClass(string nameContains, string? className)
    {
        // Dò ALL devices trước
        var txtAll = RunAndRead("pnputil", "/enum-devices");
        string found = ParseForInstanceIdBlock(txtAll, nameContains, className);
        if (!string.IsNullOrWhiteSpace(found)) return found;

        // fallback: connected
        var txt = RunAndRead("pnputil", "/enum-devices /connected");
        return ParseForInstanceIdBlock(txt, nameContains, className);
    }

    static string ParseForInstanceIdBlock(string txt, string nameContains, string? className)
    {
        if (string.IsNullOrEmpty(txt)) return "";
        string found = "";
        foreach (var raw in txt.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var blk = raw.Trim();
            // Kiểm tra Class Name (nếu yêu cầu)
            if (!string.IsNullOrEmpty(className) && blk.IndexOf("Class Name:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Skip nếu không đúng class
                var iCls = blk.IndexOf("Class Name:", StringComparison.OrdinalIgnoreCase);
                if (iCls >= 0)
                {
                    var line = blk.Substring(iCls).Split('\n').FirstOrDefault() ?? "";
                    if (line.IndexOf(className, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
            }
            // Kiểm tra tên thiết bị
            if (blk.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

            // Lấy Instance ID
            foreach (var line in blk.Split('\n'))
            {
                var i = line.IndexOf("Instance ID:", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    found = line.Substring(i + 12).Trim();
                    break;
                }
            }
            if (!string.IsNullOrWhiteSpace(found)) break;
        }
        return found;
    }

    static bool IsDeviceDisabled(string instanceId)
    {
        var txt = RunAndRead("pnputil", "/enum-devices");
        var i = txt.IndexOf(instanceId, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var around = txt.Substring(Math.Max(0, i - 200), Math.Min(600, txt.Length - Math.Max(0, i - 200)));
        return around.IndexOf("Status: Disabled", StringComparison.OrdinalIgnoreCase) >= 0
            || around.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string RunAndRead(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string s = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
        p.WaitForExit(4000);
        return s;
    }

    static void RunPnputil(string args)
    {
        Console.WriteLine("[pnputil] " + args);
        var psi = new ProcessStartInfo("pnputil", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Verb = "runas"
        };
        using var p = Process.Start(psi)!;
        Console.WriteLine(p.StandardOutput.ReadToEnd());
        var err = p.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine(err);
        p.WaitForExit();
    }

    static void TryExtendDesktop()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "displayswitch.exe",
                Arguments = "/extend",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Thread.Sleep(800);
        }
        catch { }
    }

    // (2) Tắt "Show my taskbar on all displays" (KHÔNG restart explorer)
    public static void DisableMultiMonitorTaskbar()
    {
        try
        {
            using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true)
                        ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true);
            rk.SetValue("MMTaskbarEnabled", 0, RegistryValueKind.DWord);
            Console.WriteLine("[Taskbar] MMTaskbarEnabled=0 (no explorer restart).");
        }
        catch (Exception ex) { Console.WriteLine("[Taskbar] Registry set failed: " + ex.Message); }
    }

    // (3) Chọn màn ảo và ép 4082x1532@60
    public static void ForceVirtualTo4082x1532()
    {
        // Nếu chưa thấy monitor ảo thì cố gắng extend 1 lần
        var mons = WgcInterop.ListMonitorsDXGI();
        if (!mons.Any(m => DisplayUtil.IsVirtualDisplay(m.name, m.hmon)))
        {
            TryExtendDesktop();
            Thread.Sleep(500);
            mons = WgcInterop.ListMonitorsDXGI();
        }

        int mid = MonitorDetect.PickVirtualMid(mons);
        if (mid < 0 && mons.Count > 0) mid = mons.Count - 1;

        if (mid >= 0)
        {
            var dev = mons[mid].name;
            Console.WriteLine($"[Display] Force {dev} -> 4082x1532@60");
            bool ok = DisplayUtil.ForceResolution(dev, 4082, 1532, 60);
            Console.WriteLine(ok ? "[Display] OK" : "[Display] Failed to set mode");
        }
        else Console.WriteLine("[Display] No virtual monitor found.");
    }

    // (3) Đảm bảo Extend Desktop với Virtual Monitor
    public static void EnsureExtendDesktopWithVirtual()
    {
        Console.WriteLine("[Display] Ensuring Extend Desktop with Virtual Monitor...");
        
        // Đảm bảo VDD được bật
        var mons = WgcInterop.ListMonitorsDXGI();
        if (!mons.Any(m => DisplayUtil.IsVirtualDisplay(m.name, m.hmon)))
        {
            Console.WriteLine("[Display] No virtual monitor found, attempting to extend desktop...");
            TryExtendDesktop();
            Thread.Sleep(1000);
            mons = WgcInterop.ListMonitorsDXGI();
        }

        // Kiểm tra lại sau khi extend
        if (!mons.Any(m => DisplayUtil.IsVirtualDisplay(m.name, m.hmon)))
        {
            Console.WriteLine("[Display] Virtual monitor still not detected. Trying once more...");
            TryExtendDesktop();
            Thread.Sleep(1000);
            mons = WgcInterop.ListMonitorsDXGI();
        }

        // Nếu có virtual monitor, đặt resolution 4082x1532 cho nó
        // Simply check for any monitor with virtual resolution
        var virtualMonitor = mons.FirstOrDefault(m => m.width == 4082 && m.height == 1532);
        if (virtualMonitor.name != null && virtualMonitor.width > 0)
        {
            var virtualMid = mons.IndexOf(virtualMonitor);
            if (virtualMid >= 0)
            {
                var dev = mons[virtualMid].name;
                Console.WriteLine($"[Display] Setting virtual monitor {dev} -> 4082x1532@60");
                bool ok = DisplayUtil.ForceResolution(dev, 4082, 1532, 60);
                Console.WriteLine(ok ? "[Display] ✓ Virtual monitor resolution set" : "[Display] ⚠ Failed to set virtual resolution");
            }
            
            Console.WriteLine("[Display] ✓ Extended desktop with virtual monitor ready");
            Console.WriteLine($"[Display] Total monitors detected: {mons.Count}");
            foreach (var mon in mons)
            {
                string type = (mon.width == 4082 && mon.height == 1532) ? "VIRTUAL" : "PHYSICAL";
                Console.WriteLine($"[Display]   • {mon.name} {mon.width}x{mon.height} [{type}]");
            }
        }
        else
        {
            Console.WriteLine("[Display] ⚠ Virtual monitor not available - operating in single monitor mode");
            Console.WriteLine($"[Display] Available monitors: {mons.Count}");
            foreach (var mon in mons)
            {
                Console.WriteLine($"[Display]   • {mon.name} {mon.width}x{mon.height}");
            }
        }
    }

    // (4) Đặt Text size = 125% chỉ cho virtual monitor
    public static void SetTextScale125_VirtualOnly()
    {
        try
        {
            Console.WriteLine("[DPI/TEXT] Setting 125% text scale for virtual monitor only...");
            
            // Tìm virtual monitor
            var mons = WgcInterop.ListMonitorsDXGI();
            int virtualMid = MonitorDetect.PickVirtualMid(mons);
            
            if (virtualMid < 0)
            {
                Console.WriteLine("[DPI/TEXT] No virtual monitor found - skipping text scale setting.");
                return;
            }
            
            string virtualMonitorName = mons[virtualMid].name;
            Console.WriteLine($"[DPI/TEXT] Target virtual monitor: {virtualMonitorName} (index {virtualMid})");
            
            // Lưu original DPI cho virtual monitor
            var originalDpi = TextScaleUtil.GetMonitorDpi(virtualMonitorName);
            var originalPercent = (int)(originalDpi * 100 / 96);
            
            // Set to 125% = 120 DPI
            bool success = TextScaleUtil.SetPerMonitorTextScale(virtualMonitorName, 125);
            
            if (success)
            {
                Console.WriteLine("[DPI/TEXT] ✓ 125% text scale applied to virtual monitor only.");
                Console.WriteLine($"[DPI/TEXT] Virtual monitor: {originalPercent}% → 125%");
                Console.WriteLine("[DPI/TEXT] Physical monitors remain at their original text scale.");
                
                // For better result, offer Explorer restart
                Console.WriteLine("[DPI/TEXT] 💡 TIP: For complete text scale application, consider:");
                Console.WriteLine("[DPI/TEXT]   1. Restart Explorer: Ctrl+Shift+Right Click Start → Restart Explorer");
                Console.WriteLine("[DPI/TEXT]   2. Or run: taskkill /f /im explorer.exe && start explorer.exe");
                Console.WriteLine("[DPI/TEXT]   3. Or use global 125% instead (use --global-text-scale flag)");
            }
            else
            {
                Console.WriteLine("[DPI/TEXT] ⚠ Per-monitor text scaling may not work on this Windows version.");
                Console.WriteLine("[DPI/TEXT] This feature requires Windows 8.1+ with proper DPI awareness support.");
                Console.WriteLine("[DPI/TEXT] Physical monitors remain unaffected.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DPI/TEXT] Failed to set per-monitor text scale: " + ex.Message);
        }
    }
    
    // Alternative: Global 125% text scale (if per-monitor fails)
    public static void SetTextScale125_Global()
    {
        try
        {
            Console.WriteLine("[DPI/TEXT] Setting 125% text scale globally for all monitors...");
            
            // Save original settings
            var snapshot = TextScaleUtil.Read();
            Console.WriteLine($"[DPI/TEXT] Original text scale: K1={snapshot.K1}, K2={snapshot.K2}");
            
            // Set global 125% with delays to avoid registry conflicts
            Console.WriteLine("[DPI/TEXT] Writing text scale to primary registry key...");
            TextScaleUtil.WriteDword("Control Panel\\Accessibility", "TextScaleFactor", 125);
            Thread.Sleep(500); // Allow first registry change to settle
            
            Console.WriteLine("[DPI/TEXT] Writing text scale to secondary registry key...");
            TextScaleUtil.WriteDword("Software\\Microsoft\\Accessibility", "TextScaleFactor", 125);
            Thread.Sleep(500); // Allow second registry change to settle
            
            Console.WriteLine("[DPI/TEXT] Broadcasting text scale changes...");
            // Trigger system refresh after both registry writes
            var monitors = WgcInterop.ListMonitorsDXGI();
            foreach (var mon in monitors.Take(1)) // Just trigger refresh on primary monitor
            {
                Console.WriteLine($"[DPI/TEXT] Refresh triggered on {mon.name}");
            }
            
            bool success = true;
            
            if (success)
            {
                Console.WriteLine("[DPI/TEXT] ✓ 125% text scale applied globally.");
                Console.WriteLine("[DPI/TEXT] All monitors now use 125% text scale.");
                
                // Restart Explorer for immediate effect
                Console.WriteLine("[DPI/TEXT] 💡 For immediate effect, restart Explorer:");
                Console.WriteLine("[DPI/TEXT]   Ctrl+Shift+Right Click Start → Restart Explorer");
            }
            else
            {
                Console.WriteLine("[DPI/TEXT] ⚠ Failed to set global text scale.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DPI/TEXT] Failed to set global text scale: " + ex.Message);
        }
    }

    // (4b) Kiểm tra sau setup có màn ảo không
    public static bool HasVirtualMonitorAfterSetup()
    {
        var mons = WgcInterop.ListMonitorsDXGI();
        for (int i = 0; i < mons.Count; i++)
            if (DisplayUtil.IsVirtualDisplay(mons[i].name, mons[i].hmon))
                return true;
        return false;
    }

    // (5) Trả layout 6 mảnh (1360×765, sep=1). Hàng trên: 0-1-2; Hàng dưới: 3-4-5
    public struct RectI { public int x, y, w, h; public RectI(int X, int Y, int W, int H) { x = X; y = Y; w = W; h = H; } }
    public static IReadOnlyList<RectI> GetSixTiles_1360x765_with_1px_gutter()
    {
        const int cw = 1360, ch = 765, sep = 1;
        int x0 = 0, x1 = cw + sep, x2 = cw * 2 + sep * 2; // 0, 1361, 2722
        int y0 = 0, y1 = ch + sep;                        // 0, 766
        return new[]
        {
            new RectI(x0,y0,cw,ch), // 0 (Main L)
            new RectI(x1,y0,cw,ch), // 1 (Main C)
            new RectI(x2,y0,cw,ch), // 2 (Main R)
            new RectI(x0,y1,cw,ch), // 3 (Sub L)
            new RectI(x1,y1,cw,ch), // 4 (Sub C)
            new RectI(x2,y1,cw,ch), // 5 (Sub R)
        };
    }

    // Test taskbar toggle functionality after setup is complete
    public static void TestTaskbarToggle()
    {
        try
        {
            Console.WriteLine("[TestTaskbar] Starting toggle test...");
            
            // Read current setting
            using var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false);
            var currentSetting = rk?.GetValue("MMTaskbarEnabled");
            int currentValue = currentSetting is int v ? v : 0;
            Console.WriteLine($"[TestTaskbar] Current MMTaskbarEnabled: {currentValue}");
            
            // Test 1: Enable taskbar on all displays
            Console.WriteLine("[TestTaskbar] Test 1: Enabling taskbar on all displays...");
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true)
                ?.SetValue("MMTaskbarEnabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
            
            Console.WriteLine("[TestTaskbar] ✅ Taskbar enabled on all displays");
            Thread.Sleep(2000); // Wait for UI to update
            
            // Verify it was set
            using var rk2 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false);
            var testValue = rk2?.GetValue("MMTaskbarEnabled");
            int testCurrentValue = testValue is int tv ? tv : 0;
            Console.WriteLine($"[TestTaskbar] Verified MMTaskbarEnabled after enable: {testCurrentValue}");
            
            // Test 2: Disable taskbar on all displays (back to server default)
            Console.WriteLine("[TestTaskbar] Test 2: Disabling taskbar on all displays (server default)...");
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true)
                ?.SetValue("MMTaskbarEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            
            Console.WriteLine("[TestTaskbar] ✅ Taskbar disabled - back to server default");
            Thread.Sleep(2000); // Wait for UI to update
            
            // Final verification
            using var rk3 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false);
            var finalValue = rk3?.GetValue("MMTaskbarEnabled");
            int finalCurrentValue = finalValue is int fv ? fv : 0;
            Console.WriteLine($"[TestTaskbar] Final MMTaskbarEnabled: {finalCurrentValue}");
            
            Console.WriteLine("[TestTaskbar] ✅ Toggle test completed successfully!");
            Console.WriteLine("[TestTaskbar] 📋 Summary: Taskbar can be toggled on/off while server is running");
            Console.WriteLine("[TestTaskbar] 💡 Note: Visual changes may take a moment to apply in Explorer");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestTaskbar] ❌ Toggle test failed: {ex.Message}");
            Console.WriteLine("[TestTaskbar] ℹ️ This doesn't affect server functionality.");
        }
    }
}

// ====================== Signal & REST server (giữ nguyên cấu trúc, bổ sung "single-stream") ======================
public class SignalAndRestServer
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<Guid, WebRTCStreamer_H264> _streams = new();
    private readonly ConcurrentDictionary<Guid, (int wid, bool isMonitor, int mid, WgcCapture cap)> _captures = new();

    private List<Win32.WindowInfo> _windows = new();
    private List<(IntPtr hmon, string name, int w, int h)> _monitors = new();

    public SignalAndRestServer(string prefix) { _listener = new HttpListener(); _listener.Prefixes.Add(prefix); }
    public void SetWindows(List<Win32.WindowInfo> wins) => _windows = wins;
    public void SetMonitors(List<(IntPtr hmon, string name, int w, int h)> mons) => _monitors = mons;

    public Task StartAsync() { _listener.Start(); _ = Task.Run(AcceptLoop); Console.WriteLine($"[HTTP] {string.Join(", ", _listener.Prefixes)}"); return Task.CompletedTask; }
    public async Task StopAsync()
    {
        try { _listener.Stop(); } catch { }
        foreach (var kv in _streams) { try { await kv.Value.StopAsync(); } catch { } try { kv.Value.Dispose(); } catch { } }
        foreach (var kv in _captures) { try { kv.Value.cap.Dispose(); } catch { } }
        _streams.Clear(); _captures.Clear();
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx; try { ctx = await _listener.GetContextAsync(); } catch { break; }
            var path = ctx.Request.Url!.AbsolutePath;

            if (path == "/api/monitors" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                var arr = System.Text.Json.JsonSerializer.Serialize(
                    _monitors.Select((m, i) => new { id = i, name = m.name, w = m.w, h = m.h }));
                var b = Encoding.UTF8.GetBytes(arr);
                ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.Close(); continue;
            }

            if (path == "/api/cluster" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                int midVirt = MonitorDetect.PickVirtualMid(monsNow);
                if (midVirt < 0 && _monitors.Count > 0) midVirt = _monitors.Count - 1;

                // “Cluster” yêu cầu: main = 0,1,2 là 3 mảnh trên cùng của màn ảo.
                // Ở phía client (Unity) ta sẽ dùng đơn luồng (chỉ midVirt).
                string json = $"{{\"left\":{Math.Max(0, midVirt)},\"center\":{Math.Max(0, midVirt)},\"right\":{Math.Max(0, midVirt)}}}";
                var b = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.Close(); continue;
            }

            if (ctx.Request.IsWebSocketRequest && path == "/signal")
            {
                var qs = HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
                int wid = -1, mid = -1;
                int.TryParse(qs.Get("wid"), out wid);
                int.TryParse(qs.Get("mid"), out mid);

                if (wid < 0 && mid < 0) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                if (wid >= _windows.Count && mid >= _monitors.Count) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }

                // ---------------- STEP 6: Enforce single-stream for the VIRTUAL monitor ----------------
                if (mid >= 0 && mid < _monitors.Count)
                {
                    bool isVirtual = DisplayUtil.IsVirtualDisplay(_monitors[mid].name, _monitors[mid].hmon);
                    if (isVirtual)
                    {
                        foreach (var kv in _captures.ToArray())
                        {
                            if (kv.Value.isMonitor && kv.Value.mid == mid)
                            {
                                Console.WriteLine("[Remote] Closing previous virtual-monitor stream to enforce single-stream.");
                                try { if (_streams.TryRemove(kv.Key, out var st)) { st.StopAsync().Wait(200); st.Dispose(); } } catch { }
                                try { if (_captures.TryRemove(kv.Key, out var cap)) { cap.cap.Dispose(); } } catch { }
                            }
                        }
                    }
                }

                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var id = Guid.NewGuid();
                Console.WriteLine($"[Signal] Client connected {id}, wid={wid}, mid={mid}");
                _ = Task.Run(() => HandleClient(id, wid, mid, wsCtx.WebSocket, qs));
                continue;
            }

            ctx.Response.StatusCode = 404; ctx.Response.Close();
        }
    }

    [DllImport("combase.dll")] static extern int RoInitialize(uint initType); // 1 = RO_INIT_MULTITHREADED

    private async Task HandleClient(Guid id, int wid, int mid,
        System.Net.WebSockets.WebSocket ws,
        System.Collections.Specialized.NameValueCollection qs)
    {
        CancellationTokenSource? stopCapture = null;
        Thread? capThread = null;
        var offerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // ---- RX loop (offer + input) ----
        var rxLoop = Task.Run(async () =>
        {
            var buf = new byte[128 * 1024];
            var ms = new System.IO.MemoryStream();
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                ms.Write(buf, 0, res.Count);
                if (!res.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(ms.ToArray()); ms.SetLength(0);

                if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase)) { offerTcs.TrySetResult(text.Substring(6)); continue; }
                if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("pong")),
                        System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }

                if (text.StartsWith("{\"input\":"))
                {
                    try
                    {
                        if (text.Contains("\"input\":\"move_uv\""))
                            text = System.Text.RegularExpressions.Regex.Replace(text, "(?<=\\d),(?=\\d)", ".");
                        var obj = System.Text.Json.JsonDocument.Parse(text).RootElement;
                        string kind = obj.GetProperty("input").GetString() ?? "";
                        if (kind == "move_uv")
                        {
                            float u = obj.GetProperty("u").GetSingle();
                            float v = 1f - obj.GetProperty("v").GetSingle(); // đảo V
                            string activeDisplay = (mid >= 0 && mid < _monitors.Count) ? _monitors[mid].name : "";
                            var (px, py) = InputInjector.UvToDesktop(activeDisplay, u, v);
                            InputInjector.MoveAbsolute(px, py);
                        }
                        else if (kind == "down") InputInjector.Click(true, (obj.TryGetProperty("btn", out var b) && b.GetString() == "right"));
                        else if (kind == "up") InputInjector.Click(false, (obj.TryGetProperty("btn", out var b2) && b2.GetString() == "right"));
                        else if (kind == "wheel") InputInjector.Wheel(obj.GetProperty("delta").GetInt32(),
                                                                       obj.TryGetProperty("h", out var hv) && hv.GetBoolean());
                        else if (kind == "key") InputInjector.Key((ushort)obj.GetProperty("vk").GetInt32(),
                                                                     obj.GetProperty("down").GetBoolean());
                        else if (kind == "text") InputInjector.Text(obj.GetProperty("text").GetString() ?? "");
                    }
                    catch (Exception ex) { Console.WriteLine("[INPUT] " + ex.Message); }
                }
            }
        });

        try
        {
            string offer = await offerTcs.Task;

            int fps = TryParseInt(qs.Get("fps"), 60, 5, 120);
            int kbps = TryParseInt(qs.Get("kbps"), 12000, 0, 100000);
            int crf = TryParseInt(qs.Get("crf"), 20, 0, 40);
            string preset = qs.Get("preset") ?? "veryfast";
            bool zerolat = TryParseInt(qs.Get("zerolat"), 1, 0, 1) == 1;

            var streamer = new WebRTCStreamer_H264(fps: fps, targetKbps: kbps, crf: crf, preset: preset, zerolatency: zerolat);
            await streamer.StartAsync();
            var answer = await streamer.SetRemoteOfferAndCreateAnswerAsync(offer);
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("answer:" + answer)),
                System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            _streams[id] = streamer;

            stopCapture = new CancellationTokenSource();
            capThread = new Thread(() =>
            {
                try { RoInitialize(1); } catch { }
                try { WinRT.ComWrappersSupport.InitializeComWrappers(); } catch { }
                try
                {
                    bool isMonitor = (mid >= 0 && mid < _monitors.Count);
                    WgcCapture cap = isMonitor
                        ? new WgcCapture(_monitors[mid].hmon, isMonitor: true)
                        : new WgcCapture(_windows[wid].hwnd);

                    _captures[id] = (wid, isMonitor, mid, cap);
                    cap.OnFrame += (buf, w, h, stride) =>
                    {
                        try { if (streamer.IsRunning) streamer.PushBgraBytesAsync(buf, w, h, stride); }
                        catch (Exception ex) { Console.WriteLine("[WGC->RTC] " + ex.Message); }
                    };
                    streamer.OnPeerDisconnected += () => { try { stopCapture?.Cancel(); } catch { } };

                    cap.Start();
                    stopCapture.Token.WaitHandle.WaitOne();
                }
                catch (Exception ex) { Console.WriteLine("[Capture] " + ex.Message); }
            })
            { IsBackground = true, Name = $"WGC-Capture-{(mid >= 0 ? $"mid{mid}" : $"wid{wid}")}" };
            capThread.Start();

            while (ws.State == System.Net.WebSockets.WebSocketState.Open) await Task.Delay(200);
            await rxLoop;
        }
        catch (Exception ex) { Console.WriteLine($"[Signal] {ex.Message}"); }
        finally
        {
            try { if (stopCapture != null) stopCapture.Cancel(); } catch { }
            try { if (capThread != null && capThread.IsAlive) capThread.Join(500); } catch { }

            if (_streams.TryRemove(id, out var st)) { try { st.StopAsync().Wait(500); } catch { } try { st.Dispose(); } catch { } }
            if (_captures.TryRemove(id, out var cap)) { try { cap.cap.Dispose(); } catch { } }

            try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            Console.WriteLine($"[Signal] Client disconnected {id}");
        }
    }

    static int TryParseInt(string? s, int def, int min, int max) => int.TryParse(s, out var v) ? Math.Clamp(v, min, max) : def;

    public static async Task ForceCleanupResources()
    {
        // Force cleanup any remaining threads and resources
        var tasks = new List<Task>();

        // Force garbage collection to clean up COM objects
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100);
        }

        // Try to kill any stuck processes (backup)
        try
        {
            var processes = Process.GetProcessesByName("RemotePlayServer");
            foreach (var p in processes.Where(p => p.Id != Process.GetCurrentProcess().Id))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        await Task.Delay(1000);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}

// ====================== WebRTC interface (giữ nguyên) ======================
public interface IWebRTCStreamer : IDisposable
{
    Task StartAsync();
    Task StopAsync();
    Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp);
    Task PushBgraBytesAsync(byte[] src, int width, int height, int stride);
}

// ====================== Virtual monitor heuristics (giữ nguyên, bổ sung dùng chung) ======================
static class MonitorDetect
{
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
    static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    // Physical Monitor API (fallback)
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    static bool IsLikelyVirtualByStrings(string deviceString, string deviceId)
    {
        var s = (deviceString ?? "").ToLowerInvariant();
        var id = (deviceId ?? "").ToLowerInvariant();
        string[] keywords = { "virtual", "idd", "indirect", "headless" };
        
        // DEBUG: Log the actual device ID patterns we see
        if (id.Contains("display"))
        {
            Console.WriteLine($"[DEBUG] Virtual monitor detection - DeviceString: '{deviceString}', DeviceID: '{deviceId}'");
        }
        
        // Simple and reliable: Any high-numbered display (> 10) is very likely virtual
        // Extract display number from device name: \\.\DISPLAY22 -> 22
        var displayNumMatch = System.Text.RegularExpressions.Regex.Match(deviceString, @"DISPLAY(\d+)");
        if (displayNumMatch.Success && int.TryParse(displayNumMatch.Groups[1].Value, out int displayNum))
        {
            if (displayNum > 10)
            {
                Console.WriteLine($"[DEBUG] Virtual monitor detected via display number {displayNum}: {deviceString}");
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

    /// Trả về true nếu \\.\DISPLAYx trông giống màn hình ảo.
    public static bool IsVirtualDisplay(string displayName, IntPtr hmon)
    {
        for (uint devNum = 0; ; devNum++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, devNum, ref dd, 0)) break;
            if (!string.Equals(dd.DeviceName, displayName, StringComparison.OrdinalIgnoreCase))
                continue;

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

    /// Chọn mid của màn hình ảo - monitor cuối cùng trong list được biết là virtual.
    public static int PickVirtualMid(List<(IntPtr hmon, string name, int width, int height)> mons)
    {
        if (mons == null || mons.Count == 0) return -1;

        // Simple approach: The last monitor in the list is always the virtual one
        // This is much more reliable than complex detection logic
        int lastIndex = mons.Count - 1;
        Console.WriteLine($"[PickVirtual] Selected last monitor as virtual: {mons[lastIndex].name} ({mons[lastIndex].width}x{mons[lastIndex].height})");
        return lastIndex;

        // Fallback ưu tiên kích thước panel phổ biến (đã đổi 768->765 cho trường hợp của bạn)
        int[] favW = { 1360, 1280, 1024, 800 };
        int[] favH = { 765, 720, 768, 600 };
        for (int i = 0; i < mons.Count; i++)
            for (int k = 0; k < favW.Length; k++)
                if (mons[i].width == favW[k] && mons[i].height == favH[k])
                    return i;

        return mons.Count > 0 ? mons.Count - 1 : -1;
    }
}

static class InputInjector
{
    public static System.Action<string>? OnLog;
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public int type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public int dwFlags; public int time; public IntPtr dwExtraInfo; }

    const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const int MOUSEEVENTF_MOVE = 0x0001;
    const int MOUSEEVENTF_ABSOLUTE = 0x8000;
    const int MOUSEEVENTF_LEFTDOWN = 0x0002;
    const int MOUSEEVENTF_LEFTUP = 0x0004;
    const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const int MOUSEEVENTF_RIGHTUP = 0x0010;
    const int MOUSEEVENTF_WHEEL = 0x0800;
    const int MOUSEEVENTF_HWHEEL = 0x01000;
    const int MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    const int MOUSEEVENTF_MIDDLEUP = 0x0040;
    const int KEYEVENTF_KEYUP = 0x0002;
    const int KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);

    // Map (u,v) [0..1] trên 1 monitor -> toạ độ desktop tuyệt đối
    public static void MoveRelative(int dx, int dy)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        OnLog?.Invoke($"MoveRel dx={dx} dy={dy}");
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static (int x, int y) UvToDesktop(string deviceName, float u, float v)
    {
        var (x, y, w, h, ok) = DisplayUtil.TryGetLayout(deviceName);
        if (!ok) return (0, 0);
        int px = x + Math.Clamp((int)Math.Round(u * (w - 1)), 0, Math.Max(0, w - 1));
        int py = y + Math.Clamp((int)Math.Round(v * (h - 1)), 0, Math.Max(0, h - 1));
        return (px, py);
    }

    public static void Text(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        var list = new System.Collections.Generic.List<INPUT>();
        foreach (var ch in s)
        {
            // gửi UNICODE down
            list.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,                // VK = 0 khi dùng UNICODE
                        wScan = (ushort)ch,             // mã unicode
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
            // gửi UNICODE up
            list.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }
        OnLog?.Invoke($"Text \"{s}\"");
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public static void ClickMiddle(bool down)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        OnLog?.Invoke($"Click M {(down ? "DOWN" : "UP")}");
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void Wheel(int delta, bool horizontal = false)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = delta,
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        OnLog?.Invoke($"Wheel {(horizontal ? "H" : "V")} delta={delta}");
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void MoveAbsolute(int px, int py) { OnLog?.Invoke($"SetCursorPos x={px} y={py}"); SetCursorPos(px, py); }

    public static void Click(bool down, bool right = false)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = right
                        ? (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP)
                        : (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        OnLog?.Invoke($"Click {(right ? "R" : "L")} {(down ? "DOWN" : "UP")}");
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void Key(ushort vk, bool down)
    {
        OnLog?.Invoke($"Key vk=0x{vk:X2} {(down ? "DOWN" : "UP")}");
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = down ? 0 : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    }
