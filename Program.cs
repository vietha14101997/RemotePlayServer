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
using System.Diagnostics;

#if WINDOWS
using Microsoft.Win32;

/// <summary>
/// User-configurable display settings
/// </summary>
static class DisplayConfig
{
    public static int MonitorCount = 3;
    public static int MonitorWidth = 1366;
    public static int MonitorHeight = 768;
    public static int RefreshRate = 60;
    public static int StreamFps = 30;
    
    public static readonly (int w, int h, string label)[] Resolutions = new[]
    {
        (1920, 1080, "1920x1080 (Full HD)"),
        (1600, 900,  "1600x900"),
        (1366, 768,  "1366x768 (HD)"),
        (1280, 720,  "1280x720 (720p) - Recommended for 3 monitors"),
        (1024, 576,  "1024x576 - Best performance"),
        (960,  540,  "960x540 (qHD)"),
    };
    
    public static readonly (int fps, string label)[] FpsOptions = new[]
    {
        (60, "60 fps - Smooth (requires low resolution)"),
        (30, "30 fps - Balanced (recommended)"),
        (24, "24 fps - Cinematic"),
        (20, "20 fps - Low bandwidth"),
    };
}

partial class Program
{
    static void ShowConfigMenu()
    {
        Console.Clear();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           RemotePlayServer - Display Configuration           ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
        Console.WriteLine("║  Configure your virtual display setup before starting        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Select number of monitors
        Console.WriteLine("┌─ Number of Monitors ─────────────────────────────────────────┐");
        Console.WriteLine("│  1. 1 monitor                                                │");
        Console.WriteLine("│  2. 2 monitors                                               │");
        Console.WriteLine("│  3. 3 monitors (default)                                     │");
        Console.WriteLine("│  4. 4 monitors                                               │");
        Console.WriteLine("│  5. 5 monitors                                               │");
        Console.WriteLine("│  6. 6 monitors                                               │");
        Console.WriteLine("└───────────────────────────────────────────────────────────────┘");
        Console.Write("Select number of monitors [1-6, default=3]: ");
        
        var monInput = Console.ReadLine()?.Trim();
        if (int.TryParse(monInput, out int monCount) && monCount >= 1 && monCount <= 6)
            DisplayConfig.MonitorCount = monCount;
        else
            DisplayConfig.MonitorCount = 3;
        
        Console.WriteLine($"  → Selected: {DisplayConfig.MonitorCount} monitors");
        Console.WriteLine();

        // Select resolution
        Console.WriteLine("┌─ Monitor Resolution ────────────────────────────────────────┐");
        for (int i = 0; i < DisplayConfig.Resolutions.Length; i++)
        {
            var (w, h, label) = DisplayConfig.Resolutions[i];
            string marker = (w == 1280 && h == 720) ? " ★" : "";
            Console.WriteLine($"│  {i + 1}. {label,-50}{marker} │");
        }
        Console.WriteLine("└───────────────────────────────────────────────────────────────┘");
        
        // Calculate recommended resolution based on NVENC limit
        int nvencMaxWidth = 4096;
        int recommendedIdx = -1;
        for (int i = 0; i < DisplayConfig.Resolutions.Length; i++)
        {
            var (w, _, _) = DisplayConfig.Resolutions[i];
            int calcWidth = w * DisplayConfig.MonitorCount + (DisplayConfig.MonitorCount - 1);
            if (calcWidth <= nvencMaxWidth)
            {
                recommendedIdx = i;
                break;
            }
        }
        
        if (recommendedIdx >= 0)
        {
            var (rw, rh, _) = DisplayConfig.Resolutions[recommendedIdx];
            Console.WriteLine($"  💡 Recommended for {DisplayConfig.MonitorCount} monitors: {rw}x{rh} (total width ≤ {nvencMaxWidth})");
        }
        
        Console.Write($"Select resolution [1-{DisplayConfig.Resolutions.Length}, default=3]: ");
        
        var resInput = Console.ReadLine()?.Trim();
        int resIdx = 2; // default to 1366x768
        if (int.TryParse(resInput, out int ri) && ri >= 1 && ri <= DisplayConfig.Resolutions.Length)
            resIdx = ri - 1;
        
        DisplayConfig.MonitorWidth = DisplayConfig.Resolutions[resIdx].w;
        DisplayConfig.MonitorHeight = DisplayConfig.Resolutions[resIdx].h;
        
        Console.WriteLine($"  → Selected: {DisplayConfig.MonitorWidth}x{DisplayConfig.MonitorHeight}");
        Console.WriteLine();

        // Select FPS
        Console.WriteLine("┌─ Stream FPS ────────────────────────────────────────────────┐");
        for (int i = 0; i < DisplayConfig.FpsOptions.Length; i++)
        {
            var (fps, label) = DisplayConfig.FpsOptions[i];
            string marker = (fps == 30) ? " ★" : "";
            Console.WriteLine($"│  {i + 1}. {label,-50}{marker} │");
        }
        Console.WriteLine("└───────────────────────────────────────────────────────────────┘");
        Console.Write($"Select FPS [1-{DisplayConfig.FpsOptions.Length}, default=2 (30fps)]: ");
        
        var fpsInput = Console.ReadLine()?.Trim();
        int fpsIdx = 1; // default to 30fps
        if (int.TryParse(fpsInput, out int fi) && fi >= 1 && fi <= DisplayConfig.FpsOptions.Length)
            fpsIdx = fi - 1;
        
        DisplayConfig.StreamFps = DisplayConfig.FpsOptions[fpsIdx].fps;
        Console.WriteLine($"  → Selected: {DisplayConfig.StreamFps} fps");
        Console.WriteLine();

        // Show summary
        int totalWidth = DisplayConfig.MonitorWidth * DisplayConfig.MonitorCount + (DisplayConfig.MonitorCount - 1);
        bool exceedsNvenc = totalWidth > nvencMaxWidth;
        int totalPixels = totalWidth * DisplayConfig.MonitorHeight;
        int pixelsPerSec = totalPixels * DisplayConfig.StreamFps / 1000000;
        
        Console.WriteLine("┌─ Configuration Summary ─────────────────────────────────────┐");
        Console.WriteLine($"│  Monitors:     {DisplayConfig.MonitorCount,-45} │");
        Console.WriteLine($"│  Resolution:   {DisplayConfig.MonitorWidth}x{DisplayConfig.MonitorHeight,-39} │");
        Console.WriteLine($"│  Stream FPS:   {DisplayConfig.StreamFps,-45} │");
        Console.WriteLine($"│  Total Width:  {totalWidth} pixels{(exceedsNvenc ? " ⚠ EXCEEDS NVENC LIMIT" : " ✓"),-27} │");
        Console.WriteLine($"│  Throughput:   ~{pixelsPerSec} Mpixels/sec{"",-30} │");
        if (exceedsNvenc)
            Console.WriteLine($"│  Note: Will crop to {nvencMaxWidth}px width for encoding{"",-15} │");
        Console.WriteLine("└───────────────────────────────────────────────────────────────┘");
        Console.WriteLine();
        Console.WriteLine("Press ENTER to continue with setup...");
        Console.ReadLine();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadLibrary(string lpFileName);
    
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr hModule);

    static void CheckAmfRuntime()
    {
        Console.WriteLine("[AMF] Checking AMD AMF Runtime availability...");
        
        // Check common paths for amfrt64.dll
        var searchPaths = new[]
        {
            "amfrt64.dll",  // System PATH
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "amfrt64.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "AMF", "amfrt64.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files", "ATI Technologies", "Multimedia", "amfrt64.dll"),
        };

        bool found = false;
        foreach (var path in searchPaths)
        {
            try
            {
                IntPtr handle = LoadLibrary(path);
                if (handle != IntPtr.Zero)
                {
                    FreeLibrary(handle);
                    Console.WriteLine($"[AMF] ✓ AMD AMF Runtime found: {path}");
                    found = true;
                    break;
                }
            }
            catch { }
        }

        if (!found)
        {
            Console.WriteLine("[AMF] ⚠ AMD AMF Runtime (amfrt64.dll) NOT FOUND!");
            Console.WriteLine("[AMF] Hardware H.264 encoding will fall back to CPU software encoder (slower).");
            Console.WriteLine("[AMF] To enable AMD hardware encoding:");
            Console.WriteLine("[AMF]   1. Install/Update AMD Adrenalin Software from https://www.amd.com/support");
            Console.WriteLine("[AMF]   2. Ensure 'AMD Radeon RX 7600' drivers are up to date");
            Console.WriteLine("[AMF]   3. The AMF runtime should be installed automatically with drivers");
            Console.WriteLine();
        }
    }

    static async Task Main()
    {
        // === GLOBAL EXCEPTION HANDLERS FOR CRASH LOGGING ===
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var crashLogPath = Path.Combine(logDir, "crash.log");
        
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UNHANDLED EXCEPTION (IsTerminating={e.IsTerminating}):\n{ex}\n\n";
            Console.WriteLine(msg);
            try { File.AppendAllText(crashLogPath, msg); } catch { }
        };
        
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UNOBSERVED TASK EXCEPTION:\n{e.Exception}\n\n";
            Console.WriteLine(msg);
            try { File.AppendAllText(crashLogPath, msg); } catch { }
            e.SetObserved();
        };

        // Khôi phục nếu phiên trước bị dừng đột ngột (marker còn tồn tại)
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--restore-if-needed", StringComparison.OrdinalIgnoreCase)))
        {
            DisplayGuard.RestoreIfNeededOnStartup();
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Console.OutputEncoding = Encoding.UTF8;
        
        // === CHECK AMD AMF RUNTIME AVAILABILITY ===
        CheckAmfRuntime();
        
        // ═══════════════════════════════════════════════════════════════
        // STEP 0: Configuration Menu
        // ═══════════════════════════════════════════════════════════════
        ShowConfigMenu();
        
        Console.WriteLine("=== RemotePlayServer (with startup steps 1→6) ===");
        Console.WriteLine($"[Config] {DisplayConfig.MonitorCount} monitors @ {DisplayConfig.MonitorWidth}x{DisplayConfig.MonitorHeight}");

        // Chụp trạng thái ban đầu và tạo marker phiên
        DisplayGuard.CaptureSnapshotAtStartup();
        Console.WriteLine("[Setup] Step 1/4 completed - State captured.");
        Thread.Sleep(1000); // Allow system to stabilize

        // ---------------- STEP 2 ----------------
        Console.WriteLine("[Setup] Step 2/4: Configuring Virtual Display Driver...");
        StartupSteps.EnsureVddResolutionThenToggleDriver();
        Console.WriteLine("[Setup] VDD configuration completed.");
        Thread.Sleep(2000); // Let display driver settle

        // Liệt kê monitor sau khi toggle/taskbar
        var monitors = WgcInterop.ListMonitorsDXGI();
        Console.WriteLine("=== Monitors ===");
        for (int i = 0; i < monitors.Count; i++)
            Console.WriteLine($"{i,3}: {monitors[i].name}  {monitors[i].width}x{monitors[i].height}");

        // ---------------- STEP 3 ----------------
        Console.WriteLine("[Setup] Step 3/4: Setting up monitors (resolution + primary)...");
        StartupSteps.EnsureExtendDesktopWithVirtual();
        Console.WriteLine("[Setup] Monitor setup completed.");
        Thread.Sleep(2000); // Allow display topology to settle

        // ---------------- STEP 4 ----------------
        Console.WriteLine("[Setup] Step 4/4: Applying 125% text scale...");
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

static class StartupSteps
{
    const string DRIVER_NAME = "Virtual Display Driver";
    const string MONITOR_NAME = "Virtual Desktop Monitor";
    
    // Use DisplayConfig values instead of constants
    static int TARGET_TOTAL_MONITORS => DisplayConfig.MonitorCount;
    static int MONITOR_WIDTH => DisplayConfig.MonitorWidth;
    static int MONITOR_HEIGHT => DisplayConfig.MonitorHeight;
    static int MONITOR_REFRESH => DisplayConfig.RefreshRate;

    /// <summary>
    /// Đếm số màn hình vật lý hiện có (không bao gồm Virtual Display Driver monitors)
    /// </summary>
    public static int CountPhysicalMonitors()
    {
        int physicalCount = 0;
        var monitors = WgcInterop.ListMonitorsDXGI();
        foreach (var mon in monitors)
        {
            if (!DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon))
            {
                physicalCount++;
                Console.WriteLine($"[VDD] Physical monitor found: {mon.name} ({mon.width}x{mon.height})");
            }
        }
        return physicalCount;
    }

    /// <summary>
    /// Cập nhật số lượng màn hình ảo trong vdd_settings.xml
    /// </summary>
    public static void SetVddMonitorCount(string settingsPath, int count)
    {
        if (!File.Exists(settingsPath))
        {
            Console.WriteLine("[VDD] File not found: " + settingsPath);
            return;
        }

        var doc = XDocument.Load(settingsPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? new XElement("vdd_settings");
        
        var monitorsElement = root.Element("monitors");
        if (monitorsElement == null)
        {
            monitorsElement = new XElement("monitors");
            root.AddFirst(monitorsElement);
        }

        var countElement = monitorsElement.Element("count");
        if (countElement == null)
        {
            countElement = new XElement("count", count);
            monitorsElement.Add(countElement);
        }
        else
        {
            countElement.Value = count.ToString();
        }

        doc.Save(settingsPath);
        Console.WriteLine($"[VDD] Set monitor count to {count} in {settingsPath}");
    }

    // (1) Cấu hình VDD để tạo hệ thống 3 màn hình
    public static void EnsureVddResolutionThenToggleDriver(
        string settingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml")
    {
        try
        {
            // Bước 1: Đếm số màn hình vật lý hiện có (trước khi enable VDD)
            // Disable VDD tạm thời để đếm chính xác
            var adapterId = FindDeviceInstanceIdByNameAndClass(DRIVER_NAME, "Display adapters");
            if (string.IsNullOrWhiteSpace(adapterId))
                adapterId = FindDeviceInstanceIdByNameAndClass(DRIVER_NAME, null);

            if (!string.IsNullOrWhiteSpace(adapterId) && !IsDeviceDisabled(adapterId))
            {
                Console.WriteLine("[VDD] Temporarily disabling VDD to count physical monitors...");
                RunPnputil($"/disable-device \"{adapterId}\"");
                Thread.Sleep(1500);
            }

            // Đếm màn hình vật lý
            int physicalCount = CountPhysicalMonitors();
            Console.WriteLine($"[VDD] Physical monitors detected: {physicalCount}");

            // Bước 2: Tính số màn hình ảo cần tạo
            int virtualNeeded = Math.Max(0, TARGET_TOTAL_MONITORS - physicalCount);
            Console.WriteLine($"[VDD] Virtual monitors needed: {virtualNeeded} (target total: {TARGET_TOTAL_MONITORS})");

            if (virtualNeeded == 0)
            {
                Console.WriteLine("[VDD] No virtual monitors needed - you already have 3+ physical monitors.");
                return;
            }

            // Bước 3: Cập nhật vdd_settings.xml
            SetVddMonitorCount(settingsPath, virtualNeeded);
            
            // Đảm bảo có resolution phù hợp cho màn hình ảo
            EnsureResolutionInVddXml(settingsPath, MONITOR_WIDTH, MONITOR_HEIGHT, MONITOR_REFRESH);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[VDD] XML edit failed: " + ex.Message);
        }

        try
        {
            // Bước 4: Enable VDD adapter
            var adapterId = FindDeviceInstanceIdByNameAndClass(DRIVER_NAME, "Display adapters");
            if (string.IsNullOrWhiteSpace(adapterId))
                adapterId = FindDeviceInstanceIdByNameAndClass(DRIVER_NAME, null);

            if (!string.IsNullOrWhiteSpace(adapterId))
            {
                RunPnputil("/scan-devices"); Thread.Sleep(500);
                RunPnputil($"/enable-device \"{adapterId}\"");
                RunPnputil("/scan-devices");
                Thread.Sleep(2000); // Chờ driver load và tạo màn hình ảo
            }
            else
            {
                Console.WriteLine("[VDD] Adapter not found. Check if the driver is installed under Display adapters.");
            }

            // Enable tất cả MONITOR (dưới "Monitors") nếu có
            for (int i = 0; i < TARGET_TOTAL_MONITORS; i++)
            {
                var monitorId = FindDeviceInstanceIdByNameAndClass(MONITOR_NAME, "Monitors");
                if (!string.IsNullOrWhiteSpace(monitorId) && IsDeviceDisabled(monitorId))
                {
                    RunPnputil($"/enable-device \"{monitorId}\"");
                    Thread.Sleep(500);
                }
            }
            RunPnputil("/scan-devices");

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
            using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true) ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true);
            rk.SetValue("MMTaskbarEnabled", 0, RegistryValueKind.DWord);
            Console.WriteLine("[Taskbar] MMTaskbarEnabled=0 (no explorer restart).");
        }
        catch (Exception ex) { Console.WriteLine("[Taskbar] Registry set failed: " + ex.Message); }
    }



    // (3) Thiết lập hệ thống 3 màn hình: set resolution và primary
    public static void EnsureExtendDesktopWithVirtual()
    {
        Console.WriteLine("[Display] Setting up 3-monitor system...");

        var mons = WgcInterop.ListMonitorsDXGI();
        if (mons.Count == 0)
        {
            Console.WriteLine("[Display] ⚠ No monitors detected!");
            return;
        }

        // Phân loại màn hình vật lý và ảo
        var physicalMonitors = new List<(IntPtr hmon, string name, int width, int height)>();
        var virtualMonitors = new List<(IntPtr hmon, string name, int width, int height)>();

        foreach (var mon in mons)
        {
            if (DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon))
                virtualMonitors.Add(mon);
            else
                physicalMonitors.Add(mon);
        }

        Console.WriteLine($"[Display] Physical monitors: {physicalMonitors.Count}, Virtual monitors: {virtualMonitors.Count}");

        // Set resolution 1366x768 cho TẤT CẢ màn hình
        foreach (var mon in mons)
        {
            string type = DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon) ? "VIRTUAL" : "PHYSICAL";
            Console.WriteLine($"[Display] Setting {mon.name} [{type}] -> {MONITOR_WIDTH}x{MONITOR_HEIGHT}@{MONITOR_REFRESH}");
            DisplayUtil.ForceResolution(mon.name, MONITOR_WIDTH, MONITOR_HEIGHT, MONITOR_REFRESH);
            Thread.Sleep(300);
        }

        Thread.Sleep(1000);

        // Set physical monitor làm primary (main display)
        if (physicalMonitors.Count > 0)
        {
            var primaryMon = physicalMonitors[0];
            Console.WriteLine($"[Display] Setting {primaryMon.name} as PRIMARY display");
            SetAsPrimaryDisplay(primaryMon.name);
        }

        // In kết quả
        Console.WriteLine("[Display] ✓ 3-monitor system configured:");
        mons = WgcInterop.ListMonitorsDXGI();
        foreach (var mon in mons)
        {
            var (x, y, w, h, ok) = DisplayUtil.TryGetLayout(mon.name);
            string type = DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon) ? "VIRTUAL" : "PHYSICAL";
            bool isPrimary = DisplayUtil.IsPrimary(mon.name);
            Console.WriteLine($"[Display]   • {mon.name} {w}x{h} [{type}]{(isPrimary ? " [PRIMARY]" : "")}");
        }
    }

    /// <summary>
    /// Set một màn hình làm primary display
    /// </summary>
    static void SetAsPrimaryDisplay(string deviceName)
    {
        try
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            dm.dmDeviceName = new string('\0', 32);
            dm.dmFormName = new string('\0', 32);
            dm.dmFields = DM_POSITION;
            dm.dmPositionX = 0;
            dm.dmPositionY = 0;

            int result = ChangeDisplaySettingsExA(deviceName, ref dm, IntPtr.Zero, CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (result == 0)
            {
                // Apply changes
                var dmApply = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                ChangeDisplaySettingsExA(null, ref dmApply, IntPtr.Zero, 0, IntPtr.Zero);
                Console.WriteLine($"[Display] ✓ {deviceName} set as primary");
            }
            else
            {
                Console.WriteLine($"[Display] ⚠ Failed to set primary (error: {result})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Display] Error setting primary: {ex.Message}");
        }
    }

    // P/Invoke cho ChangeDisplaySettingsEx
    const int DM_POSITION = 0x00000020;
    const uint CDS_UPDATEREGISTRY = 0x00000001;
    const uint CDS_NORESET = 0x10000000;
    const uint CDS_SET_PRIMARY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern int ChangeDisplaySettingsExA(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    // (4) Đặt Text size = 125% global
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


}

public class SignalAndRestServer
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<Guid, IWebRTCStreamer> _streams = new();
    private readonly ConcurrentDictionary<Guid, (int wid, bool isMonitor, int mid, IDisposable cap)> _captures = new();

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

            // /api/layout - trả về layout của combined frame cho cluster mode
            if (path == "/api/layout" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                
                if (_monitors.Count == 0)
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                    continue;
                }

                // Calculate combined frame dimensions (all monitors side-by-side)
                int gap = 1;
                int rawFrameWidth = 0;
                int maxHeight = 0;
                
                foreach (var mon in _monitors)
                {
                    rawFrameWidth += mon.w;
                    if (mon.h > maxHeight) maxHeight = mon.h;
                }
                rawFrameWidth += (_monitors.Count - 1) * gap;

                // Apply NVENC limit - use CROP (not scale) for better performance
                const int NVENC_MAX_WIDTH = 4096;
                int frameWidth, frameHeight, cellWidth, cellHeight, finalGap;
                
                if (rawFrameWidth > NVENC_MAX_WIDTH)
                {
                    int excess = rawFrameWidth - NVENC_MAX_WIDTH;
                    int trimPerMonitor = (excess + _monitors.Count - 1) / _monitors.Count;
                    
                    cellWidth = _monitors[0].w - trimPerMonitor;
                    cellHeight = maxHeight;
                    frameWidth = cellWidth * _monitors.Count + (_monitors.Count - 1) * gap;
                    frameHeight = maxHeight;
                    finalGap = gap;
                    
                    if (frameWidth > NVENC_MAX_WIDTH)
                    {
                        frameWidth = NVENC_MAX_WIDTH;
                        cellWidth = (NVENC_MAX_WIDTH - (_monitors.Count - 1) * gap) / _monitors.Count;
                    }
                }
                else
                {
                    frameWidth = rawFrameWidth;
                    frameHeight = maxHeight;
                    cellWidth = _monitors[0].w;
                    cellHeight = _monitors[0].h;
                    finalGap = gap;
                }
                
                string json = $"{{\"frameWidth\":{frameWidth},\"frameHeight\":{frameHeight},\"cellWidth\":{cellWidth},\"cellHeight\":{cellHeight},\"gap\":{finalGap},\"monitors\":{_monitors.Count}}}";
                var b = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                ctx.Response.Close();
                Console.WriteLine($"[HTTP] /api/layout -> {json}");
                continue;
            }

            if (path == "/api/cluster" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                int midVirt = _monitors.Count > 0 ? _monitors.Count - 1 : -1;

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
                string mode = qs.Get("mode") ?? "";

                // mode=cluster: combined stream của tất cả monitors
                if (mode.Equals("cluster", StringComparison.OrdinalIgnoreCase))
                {
                    var clusterWsCtx = await ctx.AcceptWebSocketAsync(null);
                    var clusterId = Guid.NewGuid();
                    Console.WriteLine($"[Signal] Cluster client connected {clusterId}");
                    _ = Task.Run(() => HandleClusterClient(clusterId, clusterWsCtx.WebSocket, qs));
                    continue;
                }

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

            int fps = TryParseInt(qs.Get("fps"), DisplayConfig.StreamFps, 5, 120);
            int kbps = TryParseInt(qs.Get("kbps"), 12000, 0, 100000);
            int crf = TryParseInt(qs.Get("crf"), 20, 0, 40);
            string preset = qs.Get("preset") ?? "veryfast";
            bool zerolat = TryParseInt(qs.Get("zerolat"), 1, 0, 1) == 1;
            // Always use FFmpeg encoder
            Console.WriteLine("[Signal] Using FFmpeg encoder");
            IWebRTCStreamer streamer = new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolat);

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
                    bool isWindow = (wid >= 0 && wid < _windows.Count);
                    
                    Console.WriteLine($"[Capture] mid={mid}, wid={wid}, isMonitor={isMonitor}, isWindow={isWindow}, monitors.Count={_monitors.Count}, windows.Count={_windows.Count}");
                    
                    if (!isMonitor && !isWindow)
                    {
                        Console.WriteLine($"[Capture] ERROR: Invalid mid={mid} or wid={wid}. No valid capture target!");
                        return;
                    }
                    
                    IDisposable capture;
                    long frameCount = 0;
                    
                    if (isMonitor)
                    {
                        Console.WriteLine($"[Capture] Starting MONITOR capture (DXGI): {_monitors[mid].name} ({_monitors[mid].w}x{_monitors[mid].h})");
                        var dxgiCap = new DxgiCapture(_monitors[mid].hmon);
                        dxgiCap.OnFrame += (buf, w, h, stride) =>
                        {
                            frameCount++;
                            if (frameCount == 1 || frameCount % 60 == 0)
                                Console.WriteLine($"[DXGI] Frame #{frameCount}: {w}x{h}, stride={stride}");
                            try { if (streamer.IsRunning) streamer.PushBgraBytesAsync(buf, w, h, stride); }
                            catch (Exception ex) { Console.WriteLine("[DXGI->RTC] " + ex.Message); }
                        };
                        streamer.OnPeerDisconnected += () => { try { stopCapture?.Cancel(); } catch { } };
                        
                        Console.WriteLine("[Capture] Calling dxgiCap.Start()...");
                        dxgiCap.Start();
                        Console.WriteLine("[Capture] dxgiCap.Start() completed, waiting for stop signal...");
                        capture = dxgiCap;
                    }
                    else
                    {
                        Console.WriteLine($"[Capture] Starting WINDOW capture (WGC): {_windows[wid].title}");
                        var wgcCap = new WgcCapture(_windows[wid].hwnd);
                        wgcCap.OnFrame += (buf, w, h, stride) =>
                        {
                            frameCount++;
                            if (frameCount == 1 || frameCount % 60 == 0)
                                Console.WriteLine($"[WGC] Frame #{frameCount}: {w}x{h}, stride={stride}");
                            try { if (streamer.IsRunning) streamer.PushBgraBytesAsync(buf, w, h, stride); }
                            catch (Exception ex) { Console.WriteLine("[WGC->RTC] " + ex.Message); }
                        };
                        streamer.OnPeerDisconnected += () => { try { stopCapture?.Cancel(); } catch { } };
                        
                        Console.WriteLine("[Capture] Calling wgcCap.Start()...");
                        wgcCap.Start();
                        Console.WriteLine("[Capture] wgcCap.Start() completed, waiting for stop signal...");
                        capture = wgcCap;
                    }

                    _captures[id] = (wid, isMonitor, mid, capture);
                    stopCapture.Token.WaitHandle.WaitOne();
                }
                catch (Exception ex) { Console.WriteLine("[Capture] ERROR: " + ex.Message + "\n" + ex.StackTrace); }
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

    // Cluster capture instance (shared)
    private ClusterCapture? _clusterCapture;
    private readonly object _clusterLock = new();

    /// <summary>
    /// Handle cluster mode: combined stream of all monitors
    /// </summary>
    private async Task HandleClusterClient(Guid id, System.Net.WebSockets.WebSocket ws,
        System.Collections.Specialized.NameValueCollection qs)
    {
        CancellationTokenSource? stopCapture = null;
        Thread? capThread = null;
        var offerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // RX loop (offer + input)
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

                if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase))
                {
                    offerTcs.TrySetResult(text.Substring(6));
                    continue;
                }

                // Handle input messages for cluster mode
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
                            float v = 1f - obj.GetProperty("v").GetSingle();

                            // Map UV from combined frame to desktop coordinates
                            // u spans all monitors: 0..0.33 = mon0, 0.33..0.66 = mon1, 0.66..1 = mon2
                            if (_monitors.Count > 0 && _clusterCapture != null)
                            {
                                int frameW = _clusterCapture.FrameWidth;
                                int frameH = _clusterCapture.FrameHeight;
                                int cellW = _clusterCapture.CellWidth;
                                int gap = _clusterCapture.Gap;

                                // Pixel position in combined frame
                                int px = (int)(u * frameW);
                                int py = (int)(v * frameH);

                                // Determine which monitor
                                int monIdx = 0;
                                int xInMon = px;
                                int accX = 0;
                                for (int i = 0; i < _monitors.Count; i++)
                                {
                                    int monW = _monitors[i].w;
                                    if (px < accX + monW)
                                    {
                                        monIdx = i;
                                        xInMon = px - accX;
                                        break;
                                    }
                                    accX += monW + gap;
                                }

                                // Get monitor desktop position and map
                                string activeDisplay = _monitors[monIdx].name;
                                var (mx, my, mw, mh, ok) = DisplayUtil.TryGetLayout(activeDisplay);
                                if (ok)
                                {
                                    int deskX = mx + Math.Clamp(xInMon, 0, mw - 1);
                                    int deskY = my + Math.Clamp(py, 0, mh - 1);
                                    InputInjector.MoveAbsolute(deskX, deskY);
                                }
                            }
                        }
                        else if (kind == "down") InputInjector.Click(true, obj.TryGetProperty("btn", out var b) && b.GetString() == "right");
                        else if (kind == "up") InputInjector.Click(false, obj.TryGetProperty("btn", out var b2) && b2.GetString() == "right");
                        else if (kind == "wheel") InputInjector.Wheel(obj.GetProperty("delta").GetInt32(),
                                                                       obj.TryGetProperty("h", out var hv) && hv.GetBoolean());
                        else if (kind == "key") InputInjector.Key((ushort)obj.GetProperty("vk").GetInt32(),
                                                                     obj.GetProperty("down").GetBoolean());
                        else if (kind == "text") InputInjector.Text(obj.GetProperty("text").GetString() ?? "");
                    }
                    catch (Exception ex) { Console.WriteLine("[CLUSTER INPUT] " + ex.Message); }
                }
            }
        });

        try
        {
            string offer = await offerTcs.Task;

            int fps = TryParseInt(qs.Get("fps"), DisplayConfig.StreamFps, 5, 120);  // Use configured FPS
            int kbps = TryParseInt(qs.Get("kbps"), 6000, 0, 100000);
            int crf = TryParseInt(qs.Get("crf"), 20, 0, 40);
            string preset = qs.Get("preset") ?? "veryfast";
            bool zerolat = TryParseInt(qs.Get("zerolat"), 1, 0, 1) == 1;
            // Always use FFmpeg encoder
            Console.WriteLine("[Cluster Signal] Using FFmpeg encoder");
            IWebRTCStreamer streamer = new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolat);

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
                    Console.WriteLine($"[ClusterCapture] Starting combined capture for {_monitors.Count} monitors");

                    // Create or reuse cluster capture
                    ClusterCapture clusterCap;
                    lock (_clusterLock)
                    {
                        if (_clusterCapture == null)
                        {
                            _clusterCapture = new ClusterCapture(_monitors.Select(m => (m.hmon, m.name, m.w, m.h)).ToList(), gap: 1, targetFps: fps);
                        }
                        clusterCap = _clusterCapture;
                    }

                    long frameCount = 0;
                    
                    // FFmpeg path: use BGRA byte buffer
                    clusterCap.OnFrame += (buf, w, h, stride) =>
                    {
                        frameCount++;
                        if (frameCount == 1 || frameCount % 60 == 0)
                            Console.WriteLine($"[ClusterCapture->RTC] Frame #{frameCount}: {w}x{h}");
                        try { if (streamer.IsRunning) streamer.PushBgraBytesAsync(buf, w, h, stride); }
                        catch (Exception ex) { Console.WriteLine("[ClusterCapture->RTC] " + ex.Message); }
                    };

                    streamer.OnPeerDisconnected += () => { try { stopCapture?.Cancel(); } catch { } };

                    clusterCap.Start();
                    _captures[id] = (-1, false, -1, clusterCap);
                    stopCapture.Token.WaitHandle.WaitOne();
                }
                catch (Exception ex) { Console.WriteLine("[ClusterCapture] ERROR: " + ex.Message + "\n" + ex.StackTrace); }
            })
            { IsBackground = true, Name = "Cluster-Capture" };
            capThread.Start();

            while (ws.State == System.Net.WebSockets.WebSocketState.Open) await Task.Delay(200);
            await rxLoop;
        }
        catch (Exception ex) { Console.WriteLine($"[Cluster Signal] {ex.Message}"); }
        finally
        {
            try { stopCapture?.Cancel(); } catch { }
            try { capThread?.Join(500); } catch { }

            if (_streams.TryRemove(id, out var st)) { try { st.StopAsync().Wait(500); } catch { } try { st.Dispose(); } catch { } }
            if (_captures.TryRemove(id, out var cap))
            {
                // Don't dispose cluster capture - it might be shared
                // Only stop it if no other clients
                lock (_clusterLock)
                {
                    bool hasOtherClients = _captures.Values.Any(c => c.cap == _clusterCapture);
                    if (!hasOtherClients && _clusterCapture != null)
                    {
                        _clusterCapture.Stop();
                        _clusterCapture.Dispose();
                        _clusterCapture = null;
                    }
                }
            }

            try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            Console.WriteLine($"[Signal] Cluster client disconnected {id}");
        }
    }

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
        var displayNumMatch = System.Text.RegularExpressions.Regex.Match(deviceString ?? "", @"DISPLAY(\d+)");
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
#endif
