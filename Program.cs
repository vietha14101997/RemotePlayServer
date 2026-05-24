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

    [STAThread]
    static void Main()
    {
        // === SPECIAL MODES (console-based, no WPF) ===
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--restore-if-needed", StringComparison.OrdinalIgnoreCase)))
        {
            DisplayGuard.RestoreIfNeededOnStartup();
            return;
        }

        var watchdogArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--watchdog-pid=", StringComparison.OrdinalIgnoreCase));
        if (watchdogArg != null)
        {
            RunWatchdog(watchdogArg);
            return;
        }

        // === NORMAL LAUNCH: WPF GUI ===
        var app = new RemotePlayServer.App();
        app.InitializeComponent();
        app.Run();
    }
}

#endif
