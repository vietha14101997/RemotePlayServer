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
using RemotePlayServer.Configuration;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure;
using RemotePlayServer.Application.Protocol;
using RemotePlayServer.Server;

#if WINDOWS
using Microsoft.Win32;

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

        // === USB TETHERING DETECTION ===
        // USB Tethering creates a real network interface over USB cable.
        // This allows FULL TCP+UDP communication (both signaling AND WebRTC media).
        string? usbTetheringIP = null;
        var usbTetherInfo = UsbTetheringHelper.Detect();
        if (usbTetherInfo.IsAvailable)
        {
            usbTetheringIP = usbTetherInfo.ServerIP;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[USB] USB Tethering ACTIVE! Server IP: {usbTetheringIP}");
            Console.WriteLine($"[USB] Full TCP+UDP streaming over USB cable");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine("[USB] Not detected. Enable USB Tethering on phone for USB streaming.");
        }

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

        // Tạo QRCode đơn giản: chỉ chứa WiFi IP, Port, và USB IP (nếu có)
        string usbIPJson = usbTetheringIP != null ? $",\"usbIP\":\"{usbTetheringIP}\"" : "";
        string qrData = $"{{\"ip\":\"{preferredIP}\",\"port\":\"{port}\"{usbIPJson}}}";
        Console.WriteLine();
        Console.WriteLine("=== QRCode (Scan to connect) ===");
        Console.WriteLine($"Data: {qrData}");
        QRCodeUtil.PrintQRCodeToConsole(qrData);

        // Show connection options
        Console.WriteLine();
        Console.WriteLine("=== Connection Options ===");

        // USB Tethering (full TCP+UDP over USB cable)
        if (usbTetheringIP != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [USB]  {usbTetheringIP}:{port} (Full streaming over USB cable)");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  [USB]  Not available. Enable USB Tethering on phone");
            Console.ResetColor();
        }

        // WiFi (always available)
        Console.WriteLine($"  [WiFi] {preferredIP}:{port} (Scan QR code above)");

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
            
            // Restore Windows Scale and Layout
            StartupSteps.RestoreDpiSettings();
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

    // Store original physical monitor names and resolutions before VDD is enabled
    private static readonly HashSet<string> _physicalMonitorNames = new();
    private static readonly List<(string name, int width, int height, int refreshRate)> _originalPhysicalMonitors = new();
    
    // Store original DPI settings
    private static List<(DpiScalingHelper.LUID adapterId, uint sourceId, DpiScalingHelper.DpiScalingInfo info)>? _originalDpiSettings;

    public static void RestoreDpiSettings()
    {
        if (_originalDpiSettings == null || _originalDpiSettings.Count == 0) return;

        Console.WriteLine("[Display] Restoring original Windows Scale and Layout...");
        bool allSuccess = true;
        foreach (var (adapterId, sourceId, info) in _originalDpiSettings)
        {
            if (info.IsValid)
            {
               Console.WriteLine($"[Display] Restoring Monitor {sourceId} to {info.Current}%");
               if (!DpiScalingHelper.SetDpiScaling(adapterId, sourceId, info.Current))
               {
                   allSuccess = false;
                   Console.WriteLine($"[Display] ⚠ Failed to restore Monitor {sourceId}");
               }
            }
        }
        
        if (allSuccess)
            Console.WriteLine("[Display] ✓ All monitors restored to original scale.");
        else
            Console.WriteLine("[Display] ⚠ Some monitors failed to restore.");
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

            // Save physical monitor names and resolutions BEFORE enabling VDD
            // Any monitor present now (with VDD disabled) is a physical monitor
            _physicalMonitorNames.Clear();
            _originalPhysicalMonitors.Clear();
            var monitors = WgcInterop.ListMonitorsDXGI();
            foreach (var mon in monitors)
            {
                _physicalMonitorNames.Add(mon.name);
                var mode = DisplayUtil.GetCurrentMode(mon.name);
                _originalPhysicalMonitors.Add((mon.name, mode.Width, mode.Height, mode.Frequency));
                Console.WriteLine($"[VDD] Saved physical monitor: {mon.name} ({mode.Width}x{mode.Height}@{mode.Frequency}Hz)");
            }

            // Đếm màn hình vật lý
            int physicalCount = _physicalMonitorNames.Count;
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
            
            // Đảm bảo có resolution của primary physical monitor trong VDD settings
            // VDD monitors sẽ được set giống primary physical monitor
            var primaryMon = _originalPhysicalMonitors.FirstOrDefault();
            int resW = primaryMon.width > 0 ? primaryMon.width : 1920;
            int resH = primaryMon.height > 0 ? primaryMon.height : 1080;
            int resHz = primaryMon.refreshRate > 0 ? primaryMon.refreshRate : 60;
            EnsureResolutionInVddXml(settingsPath, resW, resH, resHz);
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

                // Try pnputil first
                var result = RunPnputil($"/enable-device \"{adapterId}\"");

                // If pnputil fails (common for IddCx drivers), try alternative methods
                if (result.Contains("Failed") || result.Contains("not connected"))
                {
                    Console.WriteLine("[VDD] pnputil failed, trying devcon...");

                    // Try devcon if available
                    if (TryEnableWithDevcon(adapterId))
                    {
                        Console.WriteLine("[VDD] Device enabled via devcon");
                    }
                    else
                    {
                        // Try SetupAPI as last resort
                        Console.WriteLine("[VDD] Trying SetupAPI EnableDevice...");
                        if (TryEnableWithSetupAPI(adapterId))
                        {
                            Console.WriteLine("[VDD] Device enabled via SetupAPI");
                        }
                        else
                        {
                            Console.WriteLine("[VDD] ⚠ Could not enable VDD automatically.");
                            Console.WriteLine("[VDD] Please enable manually: Device Manager -> Display adapters -> Virtual Display Driver -> Enable device");
                        }
                    }
                }

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

    /// <summary>
    /// Try to enable device using devcon.exe (Windows Driver Kit tool)
    /// </summary>
    static bool TryEnableWithDevcon(string instanceId)
    {
        try
        {
            // Check common devcon paths
            string[] devconPaths = {
                "devcon.exe",
                @"C:\Program Files (x86)\Windows Kits\10\Tools\x64\devcon.exe",
                @"C:\Program Files\Windows Kits\10\Tools\x64\devcon.exe",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devcon.exe")
            };

            string? devconPath = devconPaths.FirstOrDefault(File.Exists);
            if (devconPath == null)
            {
                // Try to find in PATH
                var pathResult = RunAndRead("where", "devcon.exe");
                if (!string.IsNullOrWhiteSpace(pathResult) && !pathResult.Contains("Could not find"))
                    devconPath = pathResult.Trim().Split('\n').FirstOrDefault()?.Trim();
            }

            if (devconPath == null)
            {
                Console.WriteLine("[VDD] devcon.exe not found");
                return false;
            }

            Console.WriteLine($"[VDD] Using devcon: {devconPath}");
            var result = RunAndRead(devconPath, $"enable \"@{instanceId}\"");
            Console.WriteLine($"[devcon] {result}");

            return result.Contains("enabled") || result.Contains("1 device");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VDD] devcon failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Try to enable device using SetupAPI (same method Device Manager uses)
    /// </summary>
    static bool TryEnableWithSetupAPI(string instanceId)
    {
        try
        {
            // Use PowerShell's Enable-PnpDevice which wraps SetupAPI
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false -ErrorAction Stop\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Verb = "runas"
            };

            using var p = Process.Start(psi);
            if (p == null) return false;

            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);

            if (!string.IsNullOrWhiteSpace(error) && error.Contains("Generic failure"))
            {
                Console.WriteLine("[VDD] SetupAPI: Generic failure (driver may need restart)");
                return false;
            }

            // Check if device is now enabled
            Thread.Sleep(1000);
            var checkResult = RunAndRead("powershell.exe",
                $"-Command \"(Get-PnpDevice -InstanceId '{instanceId}').Status\"");

            return checkResult.Contains("OK") || checkResult.Contains("Started");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VDD] SetupAPI failed: {ex.Message}");
            return false;
        }
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

    static string RunPnputil(string args)
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
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        Console.WriteLine(output);
        if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine(err);
        p.WaitForExit();
        return output + "\n" + err;
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
        // Physical = monitors that existed BEFORE VDD was enabled (saved in _physicalMonitorNames)
        // Virtual = NEW monitors that appeared AFTER VDD was enabled
        var physicalMonitors = new List<(IntPtr hmon, string name, int width, int height)>();
        var virtualMonitors = new List<(IntPtr hmon, string name, int width, int height)>();

        foreach (var mon in mons)
        {
            if (_physicalMonitorNames.Contains(mon.name))
                physicalMonitors.Add(mon);
            else
                virtualMonitors.Add(mon); // New monitor = VDD virtual monitor
        }

        Console.WriteLine($"[Display] Physical monitors: {physicalMonitors.Count}, Virtual monitors: {virtualMonitors.Count}");

        // Get primary physical monitor's original resolution (to match VDD monitors)
        var primaryOriginal = _originalPhysicalMonitors.FirstOrDefault();
        int targetWidth = primaryOriginal.width > 0 ? primaryOriginal.width : 1920;
        int targetHeight = primaryOriginal.height > 0 ? primaryOriginal.height : 1080;
        int targetRefresh = primaryOriginal.refreshRate > 0 ? primaryOriginal.refreshRate : 60;

        // Calculate positions for virtual monitors (extend to the right of primary)
        // First, get the current position of the primary physical monitor
        int primaryX = 0, primaryY = 0;
        if (physicalMonitors.Count > 0)
        {
            var (px, py, pw, ph, ok) = DisplayUtil.TryGetLayout(physicalMonitors[0].name);
            if (ok)
            {
                primaryX = px;
                primaryY = py;
            }
        }

        // Set VDD virtual monitors to SAME resolution as primary physical monitor
        // Position them to the right of the primary (and previous virtual monitors)
        int currentX = primaryX + targetWidth; // Start right after primary
        foreach (var mon in virtualMonitors)
        {
            Console.WriteLine($"[Display] Setting {mon.name} [VIRTUAL] -> {targetWidth}x{targetHeight}@{targetRefresh}Hz at position ({currentX}, {primaryY})");
            DisplayUtil.SetResolutionAndPosition(mon.name, targetWidth, targetHeight, targetRefresh, currentX, primaryY);
            currentX += targetWidth; // Next virtual monitor goes further right
        }

        // Apply all display changes at once
        if (virtualMonitors.Count > 0)
        {
            DisplayUtil.ApplyDisplayChanges();
            Thread.Sleep(500);
        }

        // Restore original resolution for physical monitors (VDD enabling may have changed them)
        foreach (var mon in physicalMonitors)
        {
            var original = _originalPhysicalMonitors.FirstOrDefault(m => m.name == mon.name);
            if (original.name != null && (mon.width != original.width || mon.height != original.height))
            {
                Console.WriteLine($"[Display] Restoring {mon.name} [PHYSICAL] to original {original.width}x{original.height}@{original.refreshRate}Hz");
                DisplayUtil.ForceResolution(mon.name, original.width, original.height, original.refreshRate);
                Thread.Sleep(300);
            }
            else
            {
                Console.WriteLine($"[Display] Keeping {mon.name} [PHYSICAL] at {mon.width}x{mon.height}");
            }
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
        Console.WriteLine("[Display] ✓ Multi-monitor system configured:");
        mons = WgcInterop.ListMonitorsDXGI();
        foreach (var mon in mons)
        {
            var (x, y, w, h, ok) = DisplayUtil.TryGetLayout(mon.name);
            // Use saved physical monitor names for accurate detection
            string type = _physicalMonitorNames.Contains(mon.name) ? "PHYSICAL" : "VIRTUAL";
            bool isPrimary = DisplayUtil.IsPrimary(mon.name);
            Console.WriteLine($"[Display]   • {mon.name} {w}x{h} at ({x},{y}) [{type}]{(isPrimary ? " [PRIMARY]" : "")}");
        }
        
        // Set Windows Scale and Layout to 125% (system-wide) for better readability in VR
        // Using undocumented Windows API (DisplayConfigSetDeviceInfo) for immediate effect
        Console.WriteLine("[Display] Setting Windows Scale and Layout to 125%...");
        
        // Save current settings before changing
        _originalDpiSettings = DpiScalingHelper.GetAllMonitorsDpiInfo();
        
        if (DpiScalingHelper.SetAllMonitorsDpiScaling(125))
        {
            Console.WriteLine("[Display] ✓ Scale and Layout set to 125%");
        }
        else
        {
            Console.WriteLine("[Display] ⚠ Failed to set Scale and Layout");
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
}

public class SignalAndRestServer
{
    private readonly HttpListener _listener;

    private List<Win32.WindowInfo> _windows = new();
    private List<(IntPtr hmon, string name, int w, int h)> _monitors = new();

    public SignalAndRestServer(string prefix) { _listener = new HttpListener(); _listener.Prefixes.Add(prefix); }
    public void SetWindows(List<Win32.WindowInfo> wins) => _windows = wins;
    public void SetMonitors(List<(IntPtr hmon, string name, int w, int h)> mons) => _monitors = mons;

    public Task StartAsync() { _listener.Start(); _ = Task.Run(AcceptLoop); Console.WriteLine($"[HTTP] {string.Join(", ", _listener.Prefixes)}"); return Task.CompletedTask; }
    public async Task StopAsync()
    {
        try { _listener.Stop(); } catch { }
        await Task.CompletedTask;
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
                    var hwInfo = await HardwareInfoGatherer.GetHardwareInfoAsync();
                    var encoderInfo = HardwareInfoGatherer.GetEncoderInfo();
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

            if (ctx.Request.IsWebSocketRequest && path == "/signal")
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var clientId = Guid.NewGuid();
                var remoteIp = ctx.Request.RemoteEndPoint?.Address;

                // Parse query params for transport mode
                var query = ctx.Request.Url?.Query ?? "";
                var queryParams = HttpUtility.ParseQueryString(query);
                bool isUsbTransport = queryParams["transport"]?.Equals("usb", StringComparison.OrdinalIgnoreCase) == true;

                Console.WriteLine($"[Signal] Client connected {clientId}, protocol=v2, transport={( isUsbTransport ? "USB" : "WiFi")}");

                // V2 Protocol only: 3-phase connection (hardware discovery, config, streaming)
                _ = Task.Run(async () =>
                {
                    var handler = new PhaseProtocolHandler(
                        clientId, wsCtx.WebSocket, remoteIp, CancellationToken.None, isUsbTransport);
                    await handler.HandleAsync();
                });
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

#endif
