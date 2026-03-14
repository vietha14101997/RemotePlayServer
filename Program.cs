#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using System.Runtime.InteropServices;
using System.IO;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure;
using RemotePlayServer.Infrastructure.Network;
using RemotePlayServer.Configuration;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Server;
using RemotePlayServer.Application.Streaming;
using RemotePlayServer.Core;

#if WINDOWS
partial class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr hModule);

    static string GetLocalIPAddress() => NetUtil.GetPreferredLocalIP();

    /// <summary>
    /// Watchdog mode: monitor parent server PID.
    /// If the server process exits (crash, kill, etc.), immediately restore display settings.
    /// This prevents black screen when server crashes during ShowOnly/ultrawide mode.
    /// </summary>
    static void RunWatchdog(string pidArg)
    {
        var pidStr = pidArg.Split('=').LastOrDefault();
        if (!int.TryParse(pidStr, out int parentPid))
        {
            Console.Error.WriteLine("[Watchdog] Invalid PID");
            return;
        }

        try
        {
            using var parentProcess = System.Diagnostics.Process.GetProcessById(parentPid);
            // Wait for parent to exit (blocks until process terminates)
            parentProcess.WaitForExit();
        // int exitCode = parentProcess.ExitCode; // This line throws "Process was not started by this object" for attached processes
        int exitCode = -1; 

        // If session marker still exists, the server didn't clean up → crash recovery needed
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemotePlayServer");
            var sessionMarker = Path.Combine(dataDir, "session.lock");
            var snapshotPath = Path.Combine(dataDir, "display_snapshot.json");

            if (File.Exists(sessionMarker) && File.Exists(snapshotPath))
            {
                var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);
                var logMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Watchdog] Server PID {parentPid} exited (code={exitCode}). Restoring display...\n";
                try { File.AppendAllText(Path.Combine(logDir, "watchdog.log"), logMsg); } catch { }

                // Restore display settings
                DisplayGuard.RestoreIfNeededOnStartup();

                logMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Watchdog] Display restored successfully.\n";
                try { File.AppendAllText(Path.Combine(logDir, "watchdog.log"), logMsg); } catch { }
            }
        }
        catch (ArgumentException)
        {
            // Parent process already exited before we could attach
            // Try recovery anyway
            DisplayGuard.RestoreIfNeededOnStartup();
        }
        catch (Exception ex)
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            try { File.AppendAllText(Path.Combine(logDir, "watchdog.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Watchdog] Error: {ex.Message}\n"); } catch { }

            // Best effort: try recovery anyway
            try { DisplayGuard.RestoreIfNeededOnStartup(); } catch { }
        }
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

        // Check for NVIDIA NVENC
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

    /// <summary>
    /// Create a Windows Scheduled Task that runs at logon with highest privileges.
    /// This ensures display recovery after power loss/BSOD.
    /// </summary>
    static void EnsureStartupRecoveryTask()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            var taskName = "RemotePlayServerDisplayRecovery";
            var args = $"/Create /TN \"{taskName}\" /TR \"\\\"{exePath}\\\" --restore-if-needed\" /SC ONSTART /RU SYSTEM /F";

            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);

            Console.WriteLine("[System] Startup recovery task registered.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[System] Recovery task registration failed: {ex.Message}");
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

            if (e.IsTerminating)
            {
                try { DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(10)); } catch { }
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            // SIPSorcery internally fires UDP ReceiveFromAsync that become unobserved
            // when PeerConnection closes (SocketException 995). This is expected - suppress silently.
            if (e.Exception.InnerException is System.Net.Sockets.SocketException sockEx
                && sockEx.NativeErrorCode == 995)
            {
                e.SetObserved();
                return;
            }

            // SIPSorcery STUN Response Race Condition (ArgumentOutOfRangeException in GotStunResponse)
            // This is a known internal library bug - suppress as warning to avoid "crash" alarm.
            if (e.Exception.InnerException is ArgumentOutOfRangeException outOfRange
                && outOfRange.StackTrace?.Contains("SIPSorcery.Net.ChecklistEntry.GotStunResponse") == true)
            {
                Logger.Warn($"[SIPSorcery] Ignored internal library race condition: {outOfRange.Message} (GotStunResponse)");
                e.SetObserved();
                return;
            }

            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UNOBSERVED TASK EXCEPTION:\n{e.Exception}\n\n";
            Console.WriteLine(msg);
            try { File.AppendAllText(crashLogPath, msg); } catch { }
            e.SetObserved();
        };

        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--restore-if-needed", StringComparison.OrdinalIgnoreCase)))
        {
            DisplayGuard.RestoreIfNeededOnStartup();
            return;
        }

        // Watchdog mode: monitor parent PID, restore display if parent crashes
        var watchdogArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--watchdog-pid=", StringComparison.OrdinalIgnoreCase));
        if (watchdogArg != null)
        {
            RunWatchdog(watchdogArg);
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

        // === DEPENDENCY CHECK & AUTO-DOWNLOAD ===
        // Ensure FFmpeg and ADB are available, download if missing
        await RemotePlayServer.Infrastructure.DependencyManager.EnsureAllAsync();

        Console.WriteLine($"[Encoder] {DetectEncoder()}");

        // === FIREWALL CHECK ===
        // Ensure firewall rules exist for WebSocket (TCP) and LAN discovery (UDP)
        int discoveryPort = LanDiscoveryService.DefaultDiscoveryPort;
        FirewallHelper.EnsureRules(8288, discoveryPort);

        // === USB CONNECTION DETECTION ===
        // Two modes: ADB reverse (preferred, no tethering needed) and USB Tethering (RNDIS fallback)
        string? usbTetheringIP = null;
        bool adbReverseActive = false;

        var usbTetherInfo = UsbTetheringHelper.Detect();
        if (usbTetherInfo.IsAvailable)
        {
            usbTetheringIP = usbTetherInfo.ServerIP;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[USB] USB Tethering detected: {usbTetheringIP}");
            Console.ResetColor();
        }

        DisplayGuard.CaptureSnapshotAtStartup();
        EnsureStartupRecoveryTask();

        var monitors = WgcInterop.ListMonitorsDXGI();
        Console.WriteLine("=== Monitors ===");
        foreach (var mon in monitors)
        {
            string type = DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon) ? "Virtual" : "Physical";
            Console.WriteLine($"  {mon.name}: {mon.width}x{mon.height} [{type}]");
        }

        // === ADB REVERSE PORT FORWARDING ===
        // Direct USB pipe: bypasses RNDIS TCP/IP stack, lower latency
        // Android client connects to localhost:port → ADB routes through USB → PC localhost:port
        monitors = WgcInterop.ListMonitorsDXGI();
        int port = 8288;

        if (AdbPortForwardHelper.IsAdbAvailable() && AdbPortForwardHelper.IsDeviceConnected())
        {
            if (AdbPortForwardHelper.SetupReverse(port))
            {
                adbReverseActive = true;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[USB] ADB reverse ACTIVE: localhost:{port} via USB cable");
                Console.ResetColor();
            }
        }

        // Summary
        if (!adbReverseActive && !usbTetherInfo.IsAvailable)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("[USB] No USB connection. Plug in USB cable for USB streaming.");
            Console.ResetColor();
        }

        var server = new SignalServer($"http://+:{port}/");
        server.SetWindows(Win32.ListTopLevelWindows()
            .Where(w => !string.IsNullOrWhiteSpace(w.title))
            .Where(w => WgcInterop.IsCapturableWindow(w.hwnd))
            .ToList());
        server.SetMonitors(monitors.Select(m => (m.hmon, m.name, m.width, m.height)).ToList());
        InputInjector.OnLog = s => Console.WriteLine($"[INJECT] {DateTime.Now:HH:mm:ss.fff} {s}");

        await server.StartAsync();

        var preferredIP = NetUtil.GetPreferredLocalIP();
        Console.WriteLine($"[HTTP] Server: {preferredIP}:{port}");

        // === PARALLEL STARTUP: DTLS pre-warm + Config load ===
        string? authToken = null;
        string? tunnelUrl = null;
        CloudflareTunnel? tunnel = null;
        RemotePlayServer.Core.Models.InternetConfig? internetConfig = null;

        var dtlsTask = Task.Run(() => SIPSorceryStreamer.PreWarmDtls());

        var configTask = Task.Run(async () =>
        {
            try { internetConfig = await InternetManager.LoadConfigAsync(); }
            catch (Exception ex) { Console.WriteLine($"[Internet] Config load failed: {ex.Message}"); }

            // Load codec preference
            try
            {
                var codecConfigPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "codec-settings.json");
                if (File.Exists(codecConfigPath))
                {
                    var json = await File.ReadAllTextAsync(codecConfigPath);
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("preferredCodec", out var codecProp))
                    {
                        var codec = codecProp.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(codec))
                        {
                            DisplayConfig.PreferredCodec = codec;
                            Console.WriteLine($"[Codec] Preferred codec: {codec}");
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[Codec] Config load failed: {ex.Message}, using default ({DisplayConfig.PreferredCodec})"); }
        });

        await Task.WhenAll(dtlsTask, configTask);

        // === CLOUDFLARE TUNNEL (zero-config internet, no router setup needed) ===
        if (internetConfig?.Enabled == true)
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine("=== Internet Mode (Cloudflare Tunnel) ===");
                Console.WriteLine("[Tunnel] Starting tunnel...");
                tunnel = new CloudflareTunnel();
                tunnelUrl = await tunnel.StartAsync(port);
                if (tunnelUrl != null)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[Tunnel] Public URL: {tunnelUrl}");
                    Console.ResetColor();

                    if (internetConfig.RequireToken)
                        authToken = AuthTokenManager.GenerateToken();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[Tunnel] Failed to start. LAN/USB modes still available.");
                    Console.ResetColor();
                    tunnel.Dispose();
                    tunnel = null;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Tunnel] Error: {ex.Message}");
                Console.ResetColor();
                tunnel?.Dispose();
                tunnel = null;
            }
        }

        // === QR CODE ===
        string usbIPJson = usbTetheringIP != null ? $",\"usbIP\":\"{usbTetheringIP}\"" : "";
        // Note: token NOT included in QR code for security (photos could leak it)
        string tunnelJson = tunnelUrl != null ? $",\"tunnelUrl\":\"{tunnelUrl}\"" : "";
        string qrData = $"{{\"ip\":\"{preferredIP}\",\"port\":\"{port}\"{usbIPJson}{tunnelJson}}}";
        Console.WriteLine();
        Console.WriteLine("=== QRCode (Scan to connect) ===");
        Console.WriteLine($"Data: {qrData}");
        QRCodeUtil.PrintQRCodeToConsole(qrData);

        // === LAN AUTO-DISCOVERY ===
        // UDP broadcast beacon — clients find server without manual IP or QR scan
        var discovery = new LanDiscoveryService(preferredIP, port, discoveryPort);
        discovery.Start();

        Console.WriteLine();
        Console.WriteLine("=== Connection Options ===");

        if (adbReverseActive)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  [USB]  ADB reverse active — client connects via localhost:{port}");
            Console.ResetColor();
            if (usbTetheringIP != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"         Tethering also available: {usbTetheringIP}:{port}");
                Console.ResetColor();
            }
        }
        else if (usbTetheringIP != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [USB]  {usbTetheringIP}:{port} (USB Tethering)");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  [USB]  Not available. Plug in USB cable");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"  [LAN]  Auto-discovery active (UDP broadcast on port {discoveryPort})");
        Console.ResetColor();
        Console.WriteLine($"  [WiFi] {preferredIP}:{port} (Scan QR code above)");

        if (tunnelUrl != null)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            if (authToken != null)
                Console.WriteLine($"  [Internet] {tunnelUrl} (Token: {authToken})");
            else
                Console.WriteLine($"  [Internet] {tunnelUrl} (No token required)");
            Console.ResetColor();
        }
        else if (internetConfig?.Enabled == true)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  [Internet] Tunnel failed (see errors above)");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine("Server is running. Press ENTER to exit.");
        Console.ReadLine();

        // Graceful shutdown
        try
        {
            Console.WriteLine("[Shutdown] Stopping server...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StopAsync();
            Console.WriteLine("[Shutdown] Server stopped.");

            VirtualDisplayManager.RestoreDpiSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Shutdown] Server stop error: {ex.Message}");
        }

        // Cleanup LAN discovery
        discovery.Dispose();

        // Cleanup ADB reverse forwarding
        AdbPortForwardHelper.RemoveReverse();

        // Cleanup tunnel
        if (tunnel != null)
        {
            Console.WriteLine("[Shutdown] Stopping tunnel...");
            tunnel.Dispose();
            Console.WriteLine("[Shutdown] Tunnel stopped.");
        }

        try
        {
            await SignalServer.ForceCleanupResources();
        }
        catch { }

        Console.WriteLine("[Shutdown] Server exited.");
    }
}

#endif
