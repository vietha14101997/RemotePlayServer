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
using QRCoder;
using RemotePlayServer.Encoding;
using SIPSorcery.Net;

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
}

partial class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadLibrary(string lpFileName);
    
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr hModule);

    static string GetLocalIPAddress()
    {
        // Use the improved NetUtil that filters VPN adapters and prioritizes LAN
        return NetUtil.GetPreferredLocalIP();
    }

    internal static bool IsPrivateV4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        // 10.0.0.0/8
        if (b[0] == 10) return true;
        // 172.16.0.0/12
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    internal static async Task<string> MaybeResolveMdnsCandidateAsync(string candStr, int timeoutMs = 2000)
    {
        // Chrome/Edge may hide local IPs by using mDNS hostnames like "<uuid>.local".
        // SIPSorcery does not resolve these automatically; resolve via OS (Windows supports mDNS) and rewrite candidate.
        var parts = candStr.Split(' ');
        if (parts.Length < 6) return candStr;

        var addr = parts[4];
        if (!addr.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return candStr;

        Console.WriteLine($"[Cluster Signal] Attempting to resolve mDNS: '{addr}'");
        
        const int maxAttempts = 2; // Reduced attempts to speed up fallback
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var resolveTask = Dns.GetHostAddressesAsync(addr);
                var completed = await Task.WhenAny(resolveTask, Task.Delay(timeoutMs));
                if (completed != resolveTask)
                {
                    Console.WriteLine($"[Cluster Signal] mDNS resolve timed out for '{addr}' (attempt {attempt}/{maxAttempts}, timeout={timeoutMs}ms)");
                    return candStr; // Return original to trigger fallback faster
                }

                var addrs = resolveTask.Result;
                if (addrs == null || addrs.Length == 0)
                {
                    Console.WriteLine($"[Cluster Signal] mDNS resolve returned no addresses for '{addr}' (attempt {attempt}/{maxAttempts})");
                    return candStr; // Return original to trigger fallback
                }

                var chosen = addrs.FirstOrDefault(IsPrivateV4)
                          ?? addrs.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (chosen == null || !IsPrivateV4(chosen))
                {
                     Console.WriteLine($"[Cluster Signal] mDNS resolved '{addr}' -> {chosen} (Public/Invalid IP). Ignoring to prevent loopback failure.");
                     return candStr; // Return original to allow fallback to Remote IP
                }

                parts[4] = chosen.ToString();
                Console.WriteLine($"[Cluster Signal] mDNS resolved '{addr}' -> {parts[4]} after {attempt} attempt(s)");
                return string.Join(' ', parts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cluster Signal] mDNS resolve attempt {attempt}/{maxAttempts} failed for '{addr}': {ex.Message}");
                if (attempt < maxAttempts)
                {
                    await Task.Delay(100);
                }
                else
                {
                    Console.WriteLine($"[Cluster Signal] All mDNS resolve attempts failed, will use fallback for '{addr}'");
                }
            }
        }

        return candStr;
    }

    internal static string MaybeReplaceMdnsWithRemoteIp(string candStr, System.Net.IPAddress? remoteIp)
    {
        if (remoteIp == null || !IsPrivateV4(remoteIp)) return candStr;

        var parts = candStr.Split(' ');
        if (parts.Length < 6) return candStr;

        var addr = parts[4];
        if (!addr.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return candStr;

        // Replace mDNS hostname with remote IP immediately for faster connection
        parts[4] = remoteIp.ToString();
        var newCandStr = string.Join(' ', parts);
        
        Console.WriteLine($"[Cluster Signal] mDNS fallback: '{addr}' -> {parts[4]} (using remote IP)");
        Console.WriteLine($"[Cluster Signal] Rewritten candidate: {newCandStr}");
        
        return newCandStr;
    }

    static string DetectEncoder()
    {
        // Check for AMD AMF
        var amfPaths = new[]
        {
            "amfrt64.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "amfrt64.dll"),
        };
        foreach (var path in amfPaths)
        {
            try
            {
                IntPtr handle = LoadLibrary(path);
                if (handle != IntPtr.Zero)
                {
                    FreeLibrary(handle);
                    return "AMD AMF (Hardware)";
                }
            }
            catch { }
        }
        
        // Check for NVIDIA NVENC (nvEncodeAPI64.dll)
        var nvencPaths = new[]
        {
            "nvEncodeAPI64.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvEncodeAPI64.dll"),
        };
        foreach (var path in nvencPaths)
        {
            try
            {
                IntPtr handle = LoadLibrary(path);
                if (handle != IntPtr.Zero)
                {
                    FreeLibrary(handle);
                    return "NVIDIA NVENC (Hardware)";
                }
            }
            catch { }
        } 
        
        return "FFmpeg x264 (Software)";
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
            
            // Khôi phục Guard khi crash
            if (e.IsTerminating)
            {
                try { DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(10)); } catch { }
            }
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
        
        Console.WriteLine("=== RemotePlayServer ===");
        
        if (OperatingSystem.IsWindows())
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[WARN] Not running as Administrator. Input injection and functionality might be limited.");
                    Console.WriteLine("[WARN] Please restart with 'Run as Administrator'.");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("[System] Running as Administrator: YES");
                }
            }
        }
        
        Console.WriteLine($"[System] Process: {Environment.ProcessPath}");
        Console.WriteLine($"[System] Local IP: {GetLocalIPAddress()}");
        Console.WriteLine($"[Encoder] {DetectEncoder()}");

        // Chụp trạng thái ban đầu và tạo marker phiên
        DisplayGuard.CaptureSnapshotAtStartup();

        // Liệt kê monitor hiện tại
        var monitors = WgcInterop.ListMonitorsDXGI();
        Console.WriteLine("=== Monitors ===");
        foreach (var mon in monitors)
        {
            string type = DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon) ? "Virtual" : "Physical";
            Console.WriteLine($"  {mon.name}: {mon.width}x{mon.height} [{type}]");
        }

        // Chuẩn bị server
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

        // Hiện IP server và tạo QRCode - ưu tiên LAN thực (192.168.x.x), filter VPN
        var preferredIP = NetUtil.GetPreferredLocalIP();

        Console.WriteLine($"[HTTP] Server: {preferredIP}:{port}");
        
        // Tạo QRCode với IP ưu tiên và danh sách monitors
        var monitorsList = monitors.Select((m, i) => new { id = i, name = m.name, w = m.width, h = m.height });
        string monitorsJson = System.Text.Json.JsonSerializer.Serialize(monitorsList);
        string qrData = $"{{\"ip\":\"{preferredIP}\",\"port\":{port},\"monitors\":{monitorsJson}}}";
        Console.WriteLine();
        Console.WriteLine("=== QRCode (Scan to connect) ===");
        Console.WriteLine($"Data: {qrData}");
        QRCodeUtil.PrintQRCodeToConsole(qrData);
        
        Console.WriteLine();
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

        // Không khôi phục Guard khi tắt server chủ động - chỉ khôi phục khi client disconnect hoặc crash

        // Force cleanup any remaining resources
        try
        {
            await SignalAndRestServer.ForceCleanupResources();
        }
        catch { }

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

    // Thiết lập hệ thống 3 màn hình: set resolution và primary
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
        
        // Set Windows Text Scale 125% (system-wide) for better readability in VR
        Console.WriteLine("[Display] Setting Windows Text Scale to 125%...");
        TextScaleUtil.SetPercent(125);
        Console.WriteLine("[Display] ✓ Text Scale set to 125%");
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

            // Quick validation endpoint - Client gọi để kiểm tra server có alive không
            if (path == "/ping" && ctx.Request.HttpMethod == "GET")
            {
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                var body = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"version\":\"2.0\"}");
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.Close();
                continue;
            }

            if (path == "/api/monitors" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                var arr = System.Text.Json.JsonSerializer.Serialize(
                    _monitors.Select((m, i) => new { id = i, name = m.name, w = m.w, h = m.h }));
                var b = Encoding.UTF8.GetBytes(arr);
                ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.Close(); continue;
            }

            // /api/hwinfo - Return hardware info for client before WebSocket connect
            if (path == "/api/hwinfo" && ctx.Request.HttpMethod == "GET")
            {
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                try
                {
                    var hwInfo = await RemotePlayServer.Utils.HardwareInfoGatherer.GetHardwareInfoAsync();
                    var encoderInfo = RemotePlayServer.Utils.HardwareInfoGatherer.GetEncoderInfo();
                    var monsNow = WgcInterop.ListMonitorsDXGI();
                    _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();

                    var response = new
                    {
                        device = new
                        {
                            name = hwInfo.DeviceName,
                            processor = hwInfo.Processor.Name,
                            gpu = hwInfo.Gpu.Name,
                            gpuVramMB = hwInfo.Gpu.VramMB,
                            ramMB = hwInfo.Ram.TotalMB,
                            os = $"{hwInfo.Os.Name} {hwInfo.Os.Version}"
                        },
                        encoder = new
                        {
                            type = encoderInfo.Type,
                            hwAccel = encoderInfo.HwAccel
                        },
                        monitors = _monitors.Select((m, i) => new
                        {
                            id = i,
                            name = m.name,
                            w = m.w,
                            h = m.h,
                            isVirtual = DisplayUtil.IsVirtualDisplay(m.name, m.hmon)
                        }),
                        network = new
                        {
                            connectionType = hwInfo.Network.ConnectionType,
                            speedMbps = hwInfo.Network.SpeedMbps,
                            ipAddress = hwInfo.Network.IpAddress
                        },
                        timestamp = hwInfo.Timestamp
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(response);
                    var b = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    Console.WriteLine("[API] /api/hwinfo -> returned hardware info");
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 500;
                    var errorJson = $"{{\"error\":\"{ex.Message}\"}}";
                    var errorBytes = Encoding.UTF8.GetBytes(errorJson);
                    ctx.Response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
                }
                ctx.Response.Close();
                continue;
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

            // API: Toggle cursor visibility
            if (path == "/api/cursor")
            {
                // Add CORS headers
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Handle preflight OPTIONS request
                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                    continue;
                }
                
                var qs = HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
                string? showParam = qs.Get("show");
                
                if (showParam != null)
                {
                    bool show = showParam.Equals("true", StringComparison.OrdinalIgnoreCase) || showParam == "1";
                    lock (_clusterLock)
                    {
                        if (_clusterCapture != null)
                        {
                            _clusterCapture.ShowCursor = show;
                            Console.WriteLine($"[API] Cursor visibility set to: {show}");
                        }
                    }
                }
                
                bool currentState = false;
                lock (_clusterLock) { currentState = _clusterCapture?.ShowCursor ?? true; }
                
                string json = $"{{\"cursor\":{currentState.ToString().ToLower()}}}";
                var b = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                ctx.Response.Close();
                continue;
            }

            if (ctx.Request.IsWebSocketRequest && path == "/signal")
            {
                var qs = HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var clientId = Guid.NewGuid();
                var remoteIp = ctx.Request.RemoteEndPoint?.Address;
                var mode = qs.Get("mode") ?? "cluster";
                var protocol = qs.Get("protocol") ?? "v1";

                Console.WriteLine($"[Signal] Client connected {clientId}, mode={mode}, protocol={protocol}");

                // Check for v2 protocol (3-phase connection)
                if (protocol.Equals("v2", StringComparison.OrdinalIgnoreCase))
                {
                    // V2 Protocol: 3-phase connection (hardware discovery, config, streaming)
                    _ = Task.Run(async () =>
                    {
                        var handler = new RemotePlayServer.Protocol.PhaseProtocolHandler(
                            clientId, wsCtx.WebSocket, remoteIp, CancellationToken.None);
                        await handler.HandleAsync();
                    });
                }
                else if (mode.Equals("multitrack", StringComparison.OrdinalIgnoreCase))
                {
                    // Multi-track mode: N separate streams, one per monitor
                    _ = Task.Run(() => HandleMultiTrackClient(clientId, wsCtx.WebSocket, qs, remoteIp));
                }
                else
                {
                    // Default cluster mode: combined stream
                    _ = Task.Run(() => HandleClusterClient(clientId, wsCtx.WebSocket, qs, remoteIp));
                }
                continue;
            }

            // Serve static files from Web folder (webrtc_protocolv2.html, etc.)
            if (ctx.Request.HttpMethod == "GET" && !ctx.Request.IsWebSocketRequest)
            {
                var fileName = path.TrimStart('/');
                if (string.IsNullOrEmpty(fileName)) fileName = "index.html";

                // Only serve specific extensions for security
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                var allowedExtensions = new[] { ".html", ".htm", ".js", ".css", ".json", ".png", ".jpg", ".gif", ".svg", ".ico" };

                if (allowedExtensions.Contains(ext))
                {
                    var webFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web");
                    var filePath = Path.Combine(webFolder, fileName);

                    // Prevent directory traversal
                    var fullPath = Path.GetFullPath(filePath);
                    var fullWebFolder = Path.GetFullPath(webFolder);

                    if (fullPath.StartsWith(fullWebFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                    {
                        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = GetContentType(ext);

                        var fileBytes = await File.ReadAllBytesAsync(fullPath);
                        ctx.Response.ContentLength64 = fileBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
                        ctx.Response.Close();
                        Console.WriteLine($"[HTTP] Served static file: {fileName}");
                        continue;
                    }
                }
            }

            ctx.Response.StatusCode = 404; ctx.Response.Close();
        }
    }

    /// <summary>
    /// Get content type from file extension.
    /// </summary>
    private static string GetContentType(string ext) => ext switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream"
    };

    [DllImport("combase.dll")] static extern int RoInitializeNative(uint initType); // 1 = RO_INIT_MULTITHREADED

    /// <summary>
    /// Initialize Windows Runtime for the current thread (needed for WGC).
    /// </summary>
    public static int RoInitialize(uint initType) => RoInitializeNative(initType);

    static int TryParseInt(string? s, int def, int min, int max) => int.TryParse(s, out var v) ? Math.Clamp(v, min, max) : def;

    // Cluster capture instance (shared)
    private ClusterCapture? _clusterCapture;
    private readonly object _clusterLock = new();

    /// <summary>
    /// Handle cluster mode: combined stream of all monitors
    /// </summary>
    private static bool IsPrivateV4(System.Net.IPAddress? ip)
    {
        if (ip == null) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        // 10.0.0.0/8
        if (b[0] == 10) return true;
        // 172.16.0.0/12
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    private static bool CandidateIsHostPrivateV4(string cand, bool relaxedFilter = false)
    {
        if (string.IsNullOrWhiteSpace(cand)) return false;
        var s = cand.Trim();
        if (s.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!s.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)) return false;

        // CRITICAL FIX: Reject TCP candidates to match browser behavior
        // Unity generates both UDP+TCP, browser only sends UDP. TCP causes ICE failures.
        if (s.Contains(" tcp ", StringComparison.OrdinalIgnoreCase) || 
            s.Contains("tcptype", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Cluster Signal] ❌ Rejecting TCP candidate (Unity fix): {s.Substring(0, Math.Min(60, s.Length))}...");
            return false; // Reject TCP candidates
        }

        // candidate:<foundation> <component> <transport> <priority> <address> <port> typ ...
        var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return false;
        var addr = parts[4];
        if (addr.Contains(':')) {
            Console.WriteLine($"[Cluster Signal] ❌ Rejecting IPv6 candidate (not supported): {addr.Substring(0, Math.Min(30, addr.Length))}...");
            return false; // skip IPv6
        }
        if (!System.Net.IPAddress.TryParse(addr, out var ip)) return false;
        
        // Accept different candidate types based on filter mode
        bool isPrivate = IsPrivateV4(ip);
        bool isHost = s.Contains(" typ host ", StringComparison.OrdinalIgnoreCase);
        bool isSrflx = s.Contains(" typ srflx ", StringComparison.OrdinalIgnoreCase);
        bool isRelay = s.Contains(" typ relay ", StringComparison.OrdinalIgnoreCase);
        
        // For strict mode (lan=1 without relaxed): accept only host private IPv4
        if (!relaxedFilter && isHost && isPrivate) {
            Console.WriteLine($"[Cluster Signal] ✅ Accepting HOST private IPv4 candidate: {addr}");
            return true;
        }
        
        // For relaxed mode: accept both host and srflx private IPv4, even relay
        if (relaxedFilter && isPrivate && (isHost || isSrflx || isRelay)) {
            string relaxedCandidateType = isHost ? "HOST" : (isSrflx ? "SRFLX" : "RELAY");
            Console.WriteLine($"[Cluster Signal] ✅ RELAXED: Accepting {relaxedCandidateType} private IPv4 candidate: {addr}");
            return true;
        }
        
        string rejectCandidateType = isHost ? "host" : (isSrflx ? "srflx" : "other");
        Console.WriteLine($"[Cluster Signal] ❌ Rejecting candidate (type={rejectCandidateType}, private={isPrivate}): {addr.Substring(0, Math.Min(30, addr.Length))}...");
        return false;
    }

    private async Task HandleClusterClient(Guid id, System.Net.WebSockets.WebSocket ws,
        System.Collections.Specialized.NameValueCollection qs,
        System.Net.IPAddress? remoteIp)
    {
        CancellationTokenSource? stopCapture = null;
        Thread? capThread = null;
        var offerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        IWebRTCStreamer? streamer = null;
        
        // Queue for ICE candidates received before streamer is ready
        var pendingIceCandidates = new List<string>();
        var iceLock = new object();

        // Parse filtering mode once for use throughout the connection
        bool lanRequested = TryParseInt(qs.Get("lan"), 0, 0, 1) == 1;
        bool relaxedFilter = TryParseInt(qs.Get("relaxed"), 0, 0, 1) == 1;  // New: allow srflx candidates
        
        // COMPATIBILITY FIX: Always enable relaxed mode for private IPs 
        // to support both old Unity clients (without relaxed=1) and new ones
        if (IsPrivateV4(remoteIp))
        {
            relaxedFilter = true;
            Console.WriteLine($"[Cluster Signal] AUTO-ENABLE relaxed mode for private IP client (remote={remoteIp})");
        }
        
        bool hostOnlyPrivateV4 = (lanRequested || IsPrivateV4(remoteIp)) && !relaxedFilter;

        // RX loop (offer + ICE candidates + input)
        var rxLoop = Task.Run(async () =>
        {
            var buf = new byte[128 * 1024];
            var ms = new System.IO.MemoryStream();
            var iceCandidatesFromClient = new List<string>();
            var clientConnected = DateTime.Now;
            
            try
            {
                // ICE filtering mode already computed above
                
                if (hostOnlyPrivateV4)
                    Console.WriteLine($"[Cluster Signal] ICE filter: host-only private IPv4 (remote={remoteIp})");
                else if (relaxedFilter)
                    Console.WriteLine($"[Cluster Signal] ICE filter: relaxed mode - host+srflx private IPv4 (remote={remoteIp})");
                else
                    Console.WriteLine($"[Cluster Signal] ICE filter: disabled (remote={remoteIp})");
                
                // Temporarily disable auto disconnect - let server wait for candidates
                // TODO: Re-enable after debugging

                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, res.Count);
                    if (!res.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(ms.ToArray()); ms.SetLength(0);

                    // Keepalive and ping measurement. Client may send "ping" periodically to keep NAT bindings and measure RTT.
                    if (text.Length <= 16 && text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                    {
                        // Reply with pong for ping measurement
                        try
                        {
                            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("pong")), 
                                             System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        catch { }
                        continue;
                    }

                    if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase))
                    {
                        offerTcs.TrySetResult(text.Substring(6));
                        continue;
                    }

                    // Handle ICE candidates from client
                    if (text.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase) || 
                        text.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[Cluster Signal] 🔍 RECEIVED ICE FROM UNITY: '{text.Substring(0, Math.Min(60, text.Length))}...' (len={text.Length})");
                        iceCandidatesFromClient.Add(text); // Track received candidates
                        
                        // Extract candidate string - handle if prefix appears twice (Unity bug workaround)
                        string candStr = text;
                        if (candStr.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) 
                            candStr = candStr.Substring(2);
                        // Fix double "candidate:candidate:" bug
                        if (candStr.StartsWith("candidate:candidate:", StringComparison.OrdinalIgnoreCase))
                            candStr = candStr.Substring("candidate:".Length);

                        var originalCandStr = candStr;
                        candStr = await Program.MaybeResolveMdnsCandidateAsync(candStr);
                        candStr = Program.MaybeReplaceMdnsWithRemoteIp(candStr, remoteIp);
                        if (!string.Equals(candStr, originalCandStr, StringComparison.Ordinal))
                        {
                            Console.WriteLine($"[Cluster Signal] mDNS rewritten: '{originalCandStr.Substring(0, Math.Min(60, originalCandStr.Length))}...' -> '{candStr.Substring(0, Math.Min(60, candStr.Length))}...'");
                        }
                        
                        Console.WriteLine($"[Cluster Signal] Received client ICE: '{candStr.Substring(0, Math.Min(60, candStr.Length))}...' (hex={BitConverter.ToString(Encoding.UTF8.GetBytes(candStr).Take(50).ToArray())})");
                        Console.WriteLine($"[Cluster Signal] Processing received candidate - full length: {candStr.Length}");

                        if (hostOnlyPrivateV4 && !CandidateIsHostPrivateV4(candStr, relaxedFilter))
                        {
                            Console.WriteLine($"[Cluster Signal] Dropped non-host/private-v4 client ICE: '{candStr.Substring(0, Math.Min(80, candStr.Length))}...'");
                            continue;
                        }
                        
                        Console.WriteLine($"[Cluster Signal] ✅ ACCEPTED client ICE: '{candStr.Substring(0, Math.Min(80, candStr.Length))}...'");
                        Console.WriteLine($"[Cluster Signal] Total ICE from client so far: {iceCandidatesFromClient.Count}");
                        
                        // Add to peer connection if streamer is ready, otherwise queue it
                        lock (iceLock)
                        {
                            if (streamer != null)
                            {
                                try
                                {
                                    streamer.AddIceCandidate(candStr);
                                    Console.WriteLine($"[Cluster Signal] Added client ICE successfully");
                                    Console.WriteLine($"[Cluster Signal] Total ICE from client: {iceCandidatesFromClient.Count}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Cluster Signal] Add ICE error: {ex.Message}");
                                }
                            }
                            else
                            {
                                // Queue ICE candidates for later
                                pendingIceCandidates.Add(candStr);
                                Console.WriteLine($"[Cluster Signal] Queued client ICE (count={pendingIceCandidates.Count})");
                            }
                        }
                        continue;
                    }

                    // Handle end-of-candidates from client
                    if (text.StartsWith("end-of-candidates", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[Cluster Signal] Client sent end-of-candidates");
                        
                        // Trigger ICE connection establishment immediately when all candidates are added
                        _ = Task.Run(async () => {
                            await Task.Delay(1000); // Let candidates settle
                            
                            WebRTCStreamer_AmfNative? amfStreamer = null;
                            lock (iceLock)
                            {
                                if (streamer != null && streamer is WebRTCStreamer_AmfNative amf)
                                {
                                    amfStreamer = amf;
                                }
                            }
                            
                            if (amfStreamer != null)
                            {
                                // Force ICE connectivity check after end-of-candidates
                                var startTime = DateTime.Now;
                                
                                // Check ICE state after short delay
                                for (int i = 0; i < 5; i++)
                                {
                                    await Task.Delay(500);
                                    Console.WriteLine($"[Cluster Signal] ICE check #{i+1}: iceState={amfStreamer.IceConnectionState}");
                                    if (amfStreamer.IceConnectionState == RTCIceConnectionState.checking || 
                                        amfStreamer.IceConnectionState == RTCIceConnectionState.connected)
                                    {
                                        Console.WriteLine($"[Cluster Signal] ✅ ICE ESTABLISHED! i={i+1}s, state={amfStreamer.IceConnectionState}");
                                        break;
                                    }
                                }
                            }
                        });
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
            }
            catch (System.Net.Sockets.SocketException) { /* Normal disconnect */ }
            catch (System.Net.WebSockets.WebSocketException) { /* Normal disconnect */ }
            catch (OperationCanceledException) { /* Normal cancellation */ }
            catch (Exception ex) { Console.WriteLine($"[Cluster RX Loop] Error: {ex.Message}"); }
        });

        try
        {
            string offer = await offerTcs.Task;

            int fps = TryParseInt(qs.Get("fps"), DisplayConfig.StreamFps, 5, 120);
            int kbps = TryParseInt(qs.Get("kbps"), 6000, 0, 100000);
            int crf = TryParseInt(qs.Get("crf"), 20, 0, 40);
            string preset = qs.Get("preset") ?? "veryfast";
            bool zerolat = TryParseInt(qs.Get("zerolat"), 1, 0, 1) == 1;
            
            // Read display config from client
            int reqMonitors = TryParseInt(qs.Get("monitors"), 3, 1, 6);
            int reqResW = TryParseInt(qs.Get("resW"), 1366, 640, 1920);
            int reqResH = TryParseInt(qs.Get("resH"), 768, 480, 1080);
            string? preferGpu = qs.Get("preferGpu"); // intel, amd, nvidia, or null for auto
            
            // Apply display configuration if changed OR if no cluster capture is running
            bool noActiveCapture;
            lock (_clusterLock) { noActiveCapture = _clusterCapture == null; }
            
            bool configChanged = (reqMonitors != DisplayConfig.MonitorCount ||
                                  reqResW != DisplayConfig.MonitorWidth ||
                                  reqResH != DisplayConfig.MonitorHeight ||
                                  fps != DisplayConfig.StreamFps);
            
            // Force apply if no capture running (e.g., first connection or after disconnect)
            if (configChanged || noActiveCapture)
            {
                Console.WriteLine($"[Cluster Signal] Applying new display config: {reqMonitors} monitors @ {reqResW}x{reqResH}, {fps} fps");
                
                // Stop existing cluster capture if running
                lock (_clusterLock)
                {
                    if (_clusterCapture != null)
                    {
                        _clusterCapture.Stop();
                        _clusterCapture.Dispose();
                        _clusterCapture = null;
                    }
                }
                
                // Update config
                DisplayConfig.MonitorCount = reqMonitors;
                DisplayConfig.MonitorWidth = reqResW;
                DisplayConfig.MonitorHeight = reqResH;
                DisplayConfig.StreamFps = fps;
                
                // Apply VDD and resolution changes
                await Task.Run(() => {
                    StartupSteps.EnsureVddResolutionThenToggleDriver();
                    Thread.Sleep(2000);
                    StartupSteps.EnsureExtendDesktopWithVirtual();
                    Thread.Sleep(1000);
                });
                
                // Refresh monitors list
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                Console.WriteLine($"[Cluster Signal] Monitors after config: {_monitors.Count}");
            }
            
            // Use LibAv encoder (in-process FFmpeg) for lower latency
            // Set to false to use FFmpeg pipe mode instead
            bool useLibAv = true; // D3D11VA hardware frames enabled
            
            Console.WriteLine($"[Cluster Signal] Using {(useLibAv ? "LibAv (in-process)" : "FFmpeg pipe")} encoder");
            
            // Create ClusterCapture first to get D3D11 device (needed for AMF zero-copy encoder)
            ClusterCapture clusterCap;
            lock (_clusterLock)
            {
                if (_clusterCapture == null)
                {
                    var mons = WgcInterop.ListMonitorsDXGI();
                    _monitors = mons.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                    _clusterCapture = new ClusterCapture(_monitors.Select(m => (m.hmon, m.name, m.w, m.h)).ToList(), gap: 1, targetFps: fps, preferredGpu: preferGpu);
                    if (!string.IsNullOrEmpty(preferGpu))
                    {
                        Console.WriteLine($"[Cluster Signal] GPU preference: {preferGpu}");
                    }
                }
                clusterCap = _clusterCapture;
            }
            
            // Create streamer with D3D11 device (enables AMF zero-copy on AMD)
            streamer = useLibAv 
                ? EncoderFactory.CreateStreamer(fps, kbps, EncoderMode.LibAv, device: clusterCap.Device)
                : new WebRTCStreamerFFmpegWrapper(fps, kbps, crf, preset, zerolat, useNV12: true);

            // Hook ICE candidate forwarding to client via WebSocket
            // Buffer for server ICE candidates - will send after answer
            var serverIceCandidates = new List<string>();
            var answerSent = false;
            var serverIceLock = new object();

            // Variables already declared at function scope
            
            streamer.OnIceCandidate += (candidate) =>
            {
                try
                {
                    if (ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        string msg;
                        if (string.Equals(candidate, "end-of-candidates", StringComparison.OrdinalIgnoreCase))
                        {
                            msg = "end-of-candidates";
                        }
                        else
                        {
                            var c = (candidate ?? string.Empty).Trim();
                            if (c.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) c = c.Substring(2);
                            if (c.StartsWith("candidate:candidate:", StringComparison.OrdinalIgnoreCase)) c = c.Substring("candidate:".Length);
                            if (!c.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)) c = "candidate:" + c;
                            msg = c;
                        }

                        if (hostOnlyPrivateV4 && msg.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase) && !CandidateIsHostPrivateV4(msg, relaxedFilter))
                        {
                            Console.WriteLine($"[Cluster Signal] Dropped non-host/private-v4 server ICE: {msg.Substring(0, Math.Min(80, msg.Length))}...");
                            return;
                        }
                        
                        lock (serverIceLock)
                        {
                            if (answerSent)
                            {
                                // Answer already sent, send ICE immediately
                                _ = ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                                Console.WriteLine($"[Cluster Signal] Sent server ICE: {msg.Substring(0, Math.Min(50, msg.Length))}...");
                            }
                            else
                            {
                                // Queue until answer is sent
                                serverIceCandidates.Add(msg);
                                Console.WriteLine($"[Cluster Signal] Queued server ICE (count={serverIceCandidates.Count}): {msg.Substring(0, Math.Min(40, msg.Length))}...");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Cluster Signal] Send ICE error: {ex.Message}");
                }
            };

            await streamer.StartAsync();
            var answer = await streamer.SetRemoteOfferAndCreateAnswerAsync(offer);
            
            // Send answer FIRST
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("answer:" + answer)),
                System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            Console.WriteLine("[Cluster Signal] Answer sent to client");
            
            // Now flush queued server ICE candidates
            lock (serverIceLock)
            {
                answerSent = true;
                if (serverIceCandidates.Count > 0)
                {
                    Console.WriteLine($"[Cluster Signal] Flushing {serverIceCandidates.Count} queued server ICE candidates");
                    foreach (var msg in serverIceCandidates)
                    {
                        _ = ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                        Console.WriteLine($"[Cluster Signal] Sent queued server ICE: {msg.Substring(0, Math.Min(50, msg.Length))}...");
                    }
                    serverIceCandidates.Clear();
                }
            }
            
            _streams[id] = streamer;
            
            // Process any queued ICE candidates from client
            lock (iceLock)
            {
                if (pendingIceCandidates.Count > 0)
                {
                    Console.WriteLine($"[Cluster Signal] Processing {pendingIceCandidates.Count} queued client ICE candidates");
                    foreach (var candStr in pendingIceCandidates)
                    {
                        try
                        {
                            streamer.AddIceCandidate(candStr);
                            Console.WriteLine($"[Cluster Signal] Added queued ICE: {candStr.Substring(0, Math.Min(50, candStr.Length))}...");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Cluster Signal] Add queued ICE error: {ex.Message}");
                        }
                    }
                    pendingIceCandidates.Clear();
                }
            }

            stopCapture = new CancellationTokenSource();
            capThread = new Thread(() =>
            {
                try { RoInitialize(1); } catch { }
                try { WinRT.ComWrappersSupport.InitializeComWrappers(); } catch { }
                try
                {
                    Console.WriteLine($"[ClusterCapture] Starting combined capture for {_monitors.Count} monitors");

                    // ClusterCapture already created above, just use it
                    
                    // Pass D3D11 device to streamer (for compatibility)
                    streamer.SetDevice(clusterCap.Device);

                    long frameCount = 0;
                    
                    // Frame timing tracking for latency measurement
                    var frameTiming = new System.Collections.Concurrent.ConcurrentQueue<(long frameNum, long captureTime)>();
                    var lastTimingSyncTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    
                    // DISABLED: Texture zero-copy path has issues with AMF CreateSurfaceFromDX11Native
                    // AMF doesn't correctly read P-frames from NV12 texture created by Video Processor
                    // Result: video freezes after first keyframe
                    // TODO: Investigate AMF texture requirements or use different approach
                    const bool enableZeroCopyTexture = false;
                    
                    // Priority 1: TRUE ZERO-COPY - NV12 texture directly to encoder (DISABLED)
                    if (enableZeroCopyTexture && streamer.UseTextureInput && streamer.UseNV12Input)
                    {
                        Console.WriteLine("[ClusterCapture] Using TRUE ZERO-COPY texture path!");
                        clusterCap.UseNV12Output = true;
                        clusterCap.OnNV12TextureFrame += (texture, w, h, timestamp) =>
                        {
                            frameCount++;
                            frameTiming.Enqueue((frameCount, timestamp));
                            // Keep only last 100 frames in timing queue
                            while (frameTiming.Count > 100) frameTiming.TryDequeue(out _);
                            try { if (streamer.IsRunning) streamer.PushTexture(texture, w, h); }
                            catch (Exception ex) { Console.WriteLine("[ClusterCapture->RTC] " + ex.Message); }
                        };
                    }
                    // Priority 2: NV12 bytes path (GPU convert, CPU copy)
                    else if (streamer.UseNV12Input)
                    {
                        clusterCap.UseNV12Output = true;
                        clusterCap.OnNV12Frame += (buf, w, h, timestamp) =>
                        {
                            frameCount++;
                            frameTiming.Enqueue((frameCount, timestamp));
                            // Keep only last 100 frames in timing queue
                            while (frameTiming.Count > 100) frameTiming.TryDequeue(out _);
                            try { if (streamer.IsRunning) streamer.PushNV12BytesAsync(buf, w, h); }
                            catch (Exception ex) { Console.WriteLine("[ClusterCapture->RTC] " + ex.Message); }
                        };
                    }
                    // Priority 3: BGRA path (CPU color conversion in FFmpeg)
                    else
                    {
                        clusterCap.OnFrame += (buf, w, h, stride, timestamp) =>
                        {
                            frameCount++;
                            frameTiming.Enqueue((frameCount, timestamp));
                            // Keep only last 100 frames in timing queue
                            while (frameTiming.Count > 100) frameTiming.TryDequeue(out _);
                            try { if (streamer.IsRunning) streamer.PushBgraBytesAsync(buf, w, h, stride); }
                            catch (Exception ex) { Console.WriteLine("[ClusterCapture->RTC] " + ex.Message); }
                        };
                    }

                    streamer.OnPeerDisconnected += () => { try { stopCapture?.Cancel(); } catch { } };

                    clusterCap.Start();
                    _captures[id] = (-1, false, -1, clusterCap);
                    
                    // Start timing sync task to send frame timing data to client
                    var timingSyncTask = Task.Run(async () =>
                    {
                        while (!stopCapture.Token.IsCancellationRequested && ws.State == System.Net.WebSockets.WebSocketState.Open)
                        {
                            try
                            {
                                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                if (now - lastTimingSyncTime >= 1000) // Send every 1 second
                                {
                                    lastTimingSyncTime = now;
                                    // Get recent frame timing data
                                    var recentFrames = frameTiming.ToArray().TakeLast(10).ToArray();
                                    if (recentFrames.Length > 0)
                                    {
                                        var timingData = new {
                                            type = "frameTiming",
                                            serverTime = now,
                                            currentFrame = frameCount,
                                            recentFrames = recentFrames.Select(f => new { frameNum = f.frameNum, captureTime = f.captureTime }).ToArray()
                                        };
                                        var json = System.Text.Json.JsonSerializer.Serialize(timingData);
                                        await ws.SendAsync(new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(json)),
                                                         System.Net.WebSockets.WebSocketMessageType.Text, true, stopCapture.Token);
                                    }
                                }
                                await Task.Delay(500, stopCapture.Token); // Check every 500ms
                            }
                            catch (OperationCanceledException) { break; }
                            catch { }
                        }
                    });
                    
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
                        
                        // Khôi phục Guard khi không còn client nào kết nối
                        Console.WriteLine("[Guard] Restoring display settings after client disconnect...");
                        try
                        {
                            DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(15));
                            Console.WriteLine("[Guard] Display settings restored.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Guard] Restore failed: {ex.Message}");
                        }
                    }
                }
            }

            try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            Console.WriteLine($"[Signal] Cluster client disconnected {id}");
        }
    }

    // Multi-PC capture instance (shared)
    private PerMonitorCapture? _multiPCCapture;
    private readonly object _multiPCLock = new();

    /// <summary>
    /// Handle multi-PC mode: N separate PeerConnections, one per monitor.
    /// Protocol (multiplexed over single WebSocket):
    /// - Client: "offer:N:sdp" for monitor N
    /// - Server: "answer:N:sdp" for monitor N  
    /// - ICE: "candidate:N:candidate" for monitor N
    /// </summary>
    private async Task HandleMultiTrackClient(Guid id, System.Net.WebSockets.WebSocket ws,
        System.Collections.Specialized.NameValueCollection qs,
        System.Net.IPAddress? remoteIp)
    {
        CancellationTokenSource? stopCapture = null;
        Thread? capThread = null;
        RemotePlayServer.Encoding.MultiPCStreamer? streamer = null;
        
        // Pending ICE per monitor
        var pendingIce = new Dictionary<int, List<string>>();
        var iceLock = new object();
        var answersReady = new HashSet<int>();

        Console.WriteLine($"[MultiPC Signal] Client {id} connected from {remoteIp}");

        int fps = TryParseInt(qs.Get("fps"), DisplayConfig.StreamFps, 5, 120);
        int kbps = TryParseInt(qs.Get("kbps"), 4000, 0, 50000);
        int reqMonitors = TryParseInt(qs.Get("monitors"), 2, 1, 6);
        int reqResW = TryParseInt(qs.Get("resW"), 1920, 640, 1920);
        int reqResH = TryParseInt(qs.Get("resH"), 1080, 480, 1080);
        string? preferGpu = qs.Get("preferGpu");

        Console.WriteLine($"[MultiPC Signal] Config: {reqMonitors} monitors @ {reqResW}x{reqResH}, {fps}fps, {kbps}kbps/stream");

        // Apply display configuration if changed (like Cluster mode)
        bool noActiveCapture;
        lock (_multiPCLock) { noActiveCapture = _multiPCCapture == null; }
        bool configChanged = (reqMonitors != DisplayConfig.MonitorCount ||
                              reqResW != DisplayConfig.MonitorWidth ||
                              reqResH != DisplayConfig.MonitorHeight ||
                              fps != DisplayConfig.StreamFps);

        if (configChanged || noActiveCapture)
        {
            Console.WriteLine($"[MultiPC Signal] Applying new display config: {reqMonitors} monitors @ {reqResW}x{reqResH}, {fps} fps");
            
            // Stop existing capture if config changed
            lock (_multiPCLock)
            {
                if (_multiPCCapture != null && configChanged)
                {
                    Console.WriteLine("[MultiPC Signal] Stopping existing capture for reconfiguration...");
                    _multiPCCapture.Stop();
                    _multiPCCapture.Dispose();
                    _multiPCCapture = null;
                }
            }

            // Update config
            DisplayConfig.MonitorCount = reqMonitors;
            DisplayConfig.MonitorWidth = reqResW;
            DisplayConfig.MonitorHeight = reqResH;
            DisplayConfig.StreamFps = fps;

            // Apply VDD and resolution changes (same as Cluster mode)
            await Task.Run(() => {
                StartupSteps.EnsureVddResolutionThenToggleDriver();
                Thread.Sleep(2000);
                StartupSteps.EnsureExtendDesktopWithVirtual();
                Thread.Sleep(1000);
            });
        }

        // Setup monitors (refresh after VDD changes)
        var monsNow = WgcInterop.ListMonitorsDXGI();
        _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
        int actualMonitors = Math.Min(reqMonitors, _monitors.Count);
        
        Console.WriteLine($"[MultiPC Signal] Available monitors: {_monitors.Count}, using: {actualMonitors}");

        // Create capture
        PerMonitorCapture capture;
        lock (_multiPCLock)
        {
            if (_multiPCCapture == null)
            {
                _multiPCCapture = new PerMonitorCapture(
                    _monitors.Take(actualMonitors).ToList(),
                    targetFps: fps,
                    preferredGpu: preferGpu);
            }
            capture = _multiPCCapture;
        }

        // Create MultiPCStreamer with per-monitor devices for PARALLEL encoding
        // Each monitor has its own D3D11 device now (no context contention!)
        streamer = new RemotePlayServer.Encoding.MultiPCStreamer(actualMonitors, fps, kbps, capture.Device);
        
        // Wire up per-monitor devices for parallel encoding
        for (int i = 0; i < actualMonitors; i++)
        {
            var perMonDevice = capture.GetDeviceForMonitor(i);
            if (perMonDevice != null)
            {
                streamer.SetDeviceForMonitor(i, perMonDevice);
                Console.WriteLine($"[MultiPC Signal] Monitor {i}: Using dedicated D3D11 device for parallel encoding");
            }
        }

        // ICE candidate forwarding with monitor index
        streamer.OnIceCandidate += (monitorIndex, candidate) =>
        {
            try
            {
                if (ws.State != System.Net.WebSockets.WebSocketState.Open) return;
                var msg = $"candidate:{monitorIndex}:{candidate}";
                _ = ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        };

        // Auto-recovery: Request client to re-offer when PC closed abnormally
        streamer.OnMonitorNeedsReconnect += (monitorIndex) =>
        {
            try
            {
                if (ws.State != System.Net.WebSockets.WebSocketState.Open) return;
                Console.WriteLine($"[MultiPC Signal] Requesting reconnect for monitor {monitorIndex}");
                var msg = $"reconnect:{monitorIndex}";
                _ = ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        };

        // RX loop - handle multiplexed offers and ICE
        var rxLoop = Task.Run(async () =>
        {
            var buf = new byte[128 * 1024];
            var ms = new System.IO.MemoryStream();

            try
            {
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, res.Count);
                    if (!res.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(ms.ToArray()); ms.SetLength(0);

                    // Ping/pong
                    if (text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
                    {
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("pong")),
                            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                        continue;
                    }

                    // offer:N:sdp - Offer for monitor N
                    if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = text.Substring(6);
                        var colonIdx = rest.IndexOf(':');
                        if (colonIdx > 0 && int.TryParse(rest.Substring(0, colonIdx), out int monIdx))
                        {
                            var offerSdp = rest.Substring(colonIdx + 1);
                            Console.WriteLine($"[MultiPC Signal] Received offer for monitor {monIdx}");
                            
                            try
                            {
                                var answerSdp = await streamer.ProcessOfferAsync(monIdx, offerSdp, reqResW, reqResH);
                                
                                // Send answer
                                var answerMsg = $"answer:{monIdx}:{answerSdp}";
                                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(answerMsg)),
                                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                                Console.WriteLine($"[MultiPC Signal] Sent answer for monitor {monIdx}");
                                
                                lock (iceLock)
                                {
                                    answersReady.Add(monIdx);
                                    // Process pending ICE for this monitor
                                    if (pendingIce.TryGetValue(monIdx, out var pending))
                                    {
                                        foreach (var cand in pending)
                                            streamer.AddIceCandidate(monIdx, cand);
                                        pending.Clear();
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[MultiPC Signal] ProcessOffer error m{monIdx}: {ex.Message}");
                            }
                        }
                        continue;
                    }

                    // candidate:N:candidate - ICE for monitor N
                    if (text.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = text.Substring(10);
                        var colonIdx = rest.IndexOf(':');
                        if (colonIdx > 0 && int.TryParse(rest.Substring(0, colonIdx), out int monIdx))
                        {
                            var candStr = rest.Substring(colonIdx + 1);
                            candStr = await Program.MaybeResolveMdnsCandidateAsync(candStr);
                            candStr = Program.MaybeReplaceMdnsWithRemoteIp(candStr, remoteIp);
                            
                            lock (iceLock)
                            {
                                if (answersReady.Contains(monIdx))
                                {
                                    streamer.AddIceCandidate(monIdx, candStr);
                                }
                                else
                                {
                                    if (!pendingIce.ContainsKey(monIdx))
                                        pendingIce[monIdx] = new List<string>();
                                    pendingIce[monIdx].Add(candStr);
                                }
                            }
                        }
                        continue;
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[MultiPC RX] {ex.Message}"); }
        });

        // Frame timing tracking for MultiTrack
        var frameTiming = new ConcurrentQueue<(long frameNum, long captureTime)>();
        long frameCount = 0;
        long lastTimingSyncTime = 0;

        try
        {
            // Start capture thread
            stopCapture = new CancellationTokenSource();
            capThread = new Thread(() =>
            {
                try { RoInitialize(1); } catch { }
                try
                {
                    // Unified path: Use OnMonitorFrame for ALL vendors (NVIDIA now uses Compute Shader for stable texture output)
                    capture.OnMonitorFrame += (monitorIndex, nv12Texture, w, h, timestamp) =>
                    {
                        streamer?.PushTexture(monitorIndex, nv12Texture, w, h);
                        // Track frame timing (only for monitor 0 to avoid duplicates)
                        if (monitorIndex == 0)
                        {
                            var fn = Interlocked.Increment(ref frameCount);
                            frameTiming.Enqueue((fn, timestamp));
                            while (frameTiming.Count > 30) frameTiming.TryDequeue(out _);
                        }
                    };
                    
                    capture.Start();
                    stopCapture.Token.WaitHandle.WaitOne();
                }
                catch (Exception ex) { Console.WriteLine($"[MultiPC Capture] {ex.Message}"); }
            })
            { IsBackground = true, Name = "MultiPC-Capture" };
            capThread.Start();

            // Start timing sync task to send frame timing data to client (like Cluster mode)
            var timingSyncTask = Task.Run(async () =>
            {
                while (!stopCapture.Token.IsCancellationRequested && ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    try
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (now - lastTimingSyncTime >= 1000)
                        {
                            lastTimingSyncTime = now;
                            var recentFrames = frameTiming.ToArray().TakeLast(10).ToArray();
                            if (recentFrames.Length > 0)
                            {
                                var timingData = new {
                                    type = "frameTiming",
                                    serverTime = now,
                                    currentFrame = frameCount,
                                    recentFrames = recentFrames.Select(f => new { frameNum = f.frameNum, captureTime = f.captureTime }).ToArray()
                                };
                                var json = System.Text.Json.JsonSerializer.Serialize(timingData);
                                await ws.SendAsync(new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(json)),
                                    System.Net.WebSockets.WebSocketMessageType.Text, true, stopCapture.Token);
                            }
                        }
                        await Task.Delay(500, stopCapture.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            });

            // Wait for connection close
            while (ws.State == System.Net.WebSockets.WebSocketState.Open) 
                await Task.Delay(200);
            await rxLoop;
        }
        catch (Exception ex) { Console.WriteLine($"[MultiPC Signal] {ex.Message}"); }
        finally
        {
            try { stopCapture?.Cancel(); } catch { }
            try { capThread?.Join(500); } catch { }
            streamer?.Dispose();

            lock (_multiPCLock)
            {
                if (_multiPCCapture != null)
                {
                    _multiPCCapture.Stop();
                    _multiPCCapture.Dispose();
                    _multiPCCapture = null;
                    
                    // Restore display settings after client disconnect (like Cluster mode)
                    Console.WriteLine("[Guard] Restoring display settings after MultiPC client disconnect...");
                    try
                    {
                        DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(15));
                        Console.WriteLine("[Guard] Display settings restored.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Guard] Restore failed: {ex.Message}");
                    }
                }
            }

            try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            Console.WriteLine($"[Signal] MultiPC client disconnected {id}");
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
    const int MOUSEEVENTF_LEFTDOWN = 0x0002;
    const int MOUSEEVENTF_LEFTUP = 0x0004;
    const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const int MOUSEEVENTF_RIGHTUP = 0x0010;
    const int MOUSEEVENTF_WHEEL = 0x0800;
    const int MOUSEEVENTF_HWHEEL = 0x01000;
    const int KEYEVENTF_KEYUP = 0x0002;
    const int KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);

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

/// <summary>
/// Utility để tạo QRCode và in ra console
/// </summary>
static class QRCodeUtil
{
    public static void PrintQRCodeToConsole(string data)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(data, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            
            // Sử dụng Unicode blocks cho QR đẹp hơn trên console
            var qrString = qrCode.GetGraphicSmall();
            Console.WriteLine(qrString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QRCode] Failed to generate: {ex.Message}");
            Console.WriteLine($"[QRCode] Raw data: {data}");
        }
    }
}
#endif
