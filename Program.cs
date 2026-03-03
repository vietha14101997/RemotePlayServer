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
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Server;
using RemotePlayServer.Application.Streaming;

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
            int exitCode = parentProcess.ExitCode;

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
            var args = $"/Create /TN \"{taskName}\" /TR \"\\\"{exePath}\\\" --restore-if-needed\" /SC ONLOGON /RL HIGHEST /F";

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
        Console.WriteLine($"[Encoder] {DetectEncoder()}");

        // === USB TETHERING DETECTION ===
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

        DisplayGuard.CaptureSnapshotAtStartup();
        EnsureStartupRecoveryTask();

        var monitors = WgcInterop.ListMonitorsDXGI();
        Console.WriteLine("=== Monitors ===");
        foreach (var mon in monitors)
        {
            string type = DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon) ? "Virtual" : "Physical";
            Console.WriteLine($"  {mon.name}: {mon.width}x{mon.height} [{type}]");
        }

        monitors = WgcInterop.ListMonitorsDXGI();
        int port = 8288;
        var server = new SignalServer($"http://+:{port}/");
        server.SetWindows(Win32.ListTopLevelWindows()
            .Where(w => !string.IsNullOrWhiteSpace(w.title))
            .Where(w => WgcInterop.IsCapturableWindow(w.hwnd))
            .ToList());
        server.SetMonitors(monitors.Select(m => (m.hmon, m.name, m.width, m.height)).ToList());
        InputInjector.OnLog = s => Console.WriteLine($"[INJECT] {DateTime.Now:HH:mm:ss.fff} {s}");

        await server.StartAsync();

        // Pre-warm DTLS/BouncyCastle crypto before first client connects.
        // Without this, the first DTLS handshake is too slow and client times out.
        SIPSorceryStreamer.PreWarmDtls();

        var preferredIP = NetUtil.GetPreferredLocalIP();
        Console.WriteLine($"[HTTP] Server: {preferredIP}:{port}");

        string usbIPJson = usbTetheringIP != null ? $",\"usbIP\":\"{usbTetheringIP}\"" : "";
        string qrData = $"{{\"ip\":\"{preferredIP}\",\"port\":\"{port}\"{usbIPJson}}}";
        Console.WriteLine();
        Console.WriteLine("=== QRCode (Scan to connect) ===");
        Console.WriteLine($"Data: {qrData}");
        QRCodeUtil.PrintQRCodeToConsole(qrData);

        Console.WriteLine();
        Console.WriteLine("=== Connection Options ===");

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

        Console.WriteLine($"  [WiFi] {preferredIP}:{port} (Scan QR code above)");

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

        try
        {
            await SignalServer.ForceCleanupResources();
        }
        catch { }

        Console.WriteLine("[Shutdown] Server exited.");
    }
}

#endif
