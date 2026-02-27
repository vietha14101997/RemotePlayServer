#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using RemotePlayServer.Configuration;
using RemotePlayServer.Infrastructure.Capture;

namespace RemotePlayServer.Infrastructure.Display;

/// <summary>
/// Manages Virtual Display Driver (VDD) setup, multi-monitor configuration,
/// DPI scaling, and display topology for the streaming server.
/// Uses named pipe IPC (fast path) with pnputil fallback (slow path).
/// </summary>
static class VirtualDisplayManager
{
    const string DRIVER_NAME = "Virtual Display Driver";
    const string MONITOR_NAME = "Virtual Desktop Monitor";
    const string VDD_PIPE_NAME = "MTTVirtualDisplayPipe";

    static int TARGET_TOTAL_MONITORS => DisplayConfig.MonitorCount;
    static int MONITOR_REFRESH => DisplayConfig.RefreshRate;

    // Physical monitor state (populated during setup)
    private static readonly HashSet<string> _physicalMonitorNames = new();
    private static readonly List<(string name, int width, int height, int refreshRate)> _originalPhysicalMonitors = new();

    // Original DPI settings for restore on shutdown
    private static List<(DpiScalingHelper.LUID adapterId, uint sourceId, DpiScalingHelper.DpiScalingInfo info)>? _originalDpiSettings;

    // Cached adapter instance ID (doesn't change between runs)
    private static string? _cachedAdapterId;

    internal static IReadOnlySet<string> PhysicalMonitorNames => _physicalMonitorNames;

    // ================================================================
    // Public API
    // ================================================================

    public static int CountPhysicalMonitors()
    {
        int count = 0;
        foreach (var mon in WgcInterop.ListMonitorsDXGI())
        {
            if (!DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon))
            {
                count++;
                Console.WriteLine($"[VDD] Physical monitor found: {mon.name} ({mon.width}x{mon.height})");
            }
        }
        return count;
    }

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
                    Console.WriteLine($"[Display] Failed to restore Monitor {sourceId}");
                }
            }
        }
        Console.WriteLine(allSuccess
            ? "[Display] All monitors restored to original scale."
            : "[Display] Some monitors failed to restore.");
    }

    /// <summary>
    /// Main entry point: configure VDD monitor count and resolution.
    /// Fast path: named pipe SETDISPLAYCOUNT (~0.5s).
    /// Slow path: pnputil disable/enable fallback (~5s).
    /// </summary>
    public static void EnsureVddResolutionThenToggleDriver(
        string settingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml")
    {
        // Step 1: Snapshot physical monitors (uses IsVirtualDisplay, no disable needed)
        SnapshotPhysicalMonitors();

        int physicalCount = _physicalMonitorNames.Count;
        int virtualNeeded = Math.Max(0, TARGET_TOTAL_MONITORS - physicalCount);
        Console.WriteLine($"[VDD] Physical: {physicalCount}, Virtual needed: {virtualNeeded} (target: {TARGET_TOTAL_MONITORS})");

        if (virtualNeeded == 0)
        {
            Console.WriteLine("[VDD] No virtual monitors needed.");
            return;
        }

        // Step 2: Ensure resolution exists in XML (needed by both paths)
        try
        {
            var primary = _originalPhysicalMonitors.FirstOrDefault();
            int resW = primary.width > 0 ? primary.width : 1920;
            int resH = primary.height > 0 ? primary.height : 1080;
            int resHz = primary.refreshRate > 0 ? primary.refreshRate : 60;
            EnsureResolutionInVddXml(settingsPath, resW, resH, resHz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VDD] XML resolution edit failed: {ex.Message}");
        }

        // Step 3: Try fast path — named pipe IPC
        if (TrySetDisplayCountViaPipe(virtualNeeded))
        {
            Console.WriteLine("[VDD] Display count set via pipe, waiting for monitors...");
            WaitForMonitorCount(TARGET_TOTAL_MONITORS, timeoutMs: 5000);
            TryExtendDesktop();
            return;
        }

        // Step 4: Slow path — XML edit + pnputil disable/enable
        Console.WriteLine("[VDD] Pipe unavailable, using pnputil fallback...");
        try
        {
            SetVddMonitorCount(settingsPath, virtualNeeded);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VDD] XML count edit failed: {ex.Message}");
        }
        ToggleVddViaPnputil();
    }

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
            monitorsElement.Add(new XElement("count", count));
        else
            countElement.Value = count.ToString();

        doc.Save(settingsPath);
        Console.WriteLine($"[VDD] Set monitor count to {count} in {settingsPath}");
    }

    public static void EnsureExtendDesktopWithVirtual()
    {
        Console.WriteLine("[Display] Setting up multi-monitor system...");

        var mons = WgcInterop.ListMonitorsDXGI();
        if (mons.Count == 0)
        {
            Console.WriteLine("[Display] No monitors detected!");
            return;
        }

        var physicalMonitors = new List<(IntPtr hmon, string name, int width, int height)>();
        var virtualMonitors = new List<(IntPtr hmon, string name, int width, int height)>();

        foreach (var mon in mons)
        {
            if (_physicalMonitorNames.Contains(mon.name))
                physicalMonitors.Add(mon);
            else
                virtualMonitors.Add(mon);
        }

        Console.WriteLine($"[Display] Physical: {physicalMonitors.Count}, Virtual: {virtualMonitors.Count}");

        var primaryOriginal = _originalPhysicalMonitors.FirstOrDefault();
        int targetWidth = primaryOriginal.width > 0 ? primaryOriginal.width : 1920;
        int targetHeight = primaryOriginal.height > 0 ? primaryOriginal.height : 1080;
        int targetRefresh = primaryOriginal.refreshRate > 0 ? primaryOriginal.refreshRate : 60;

        int primaryX = 0, primaryY = 0;
        if (physicalMonitors.Count > 0)
        {
            var (px, py, pw, ph, ok) = DisplayUtil.TryGetLayout(physicalMonitors[0].name);
            if (ok) { primaryX = px; primaryY = py; }
        }

        int currentX = primaryX + targetWidth;
        foreach (var mon in virtualMonitors)
        {
            Console.WriteLine($"[Display] Setting {mon.name} [VIRTUAL] -> {targetWidth}x{targetHeight}@{targetRefresh}Hz at ({currentX}, {primaryY})");
            DisplayUtil.SetResolutionAndPosition(mon.name, targetWidth, targetHeight, targetRefresh, currentX, primaryY);
            currentX += targetWidth;
        }

        if (virtualMonitors.Count > 0)
        {
            DisplayUtil.ApplyDisplayChanges();
            Thread.Sleep(500);
        }

        foreach (var mon in physicalMonitors)
        {
            var original = _originalPhysicalMonitors.FirstOrDefault(m => m.name == mon.name);
            if (original.name != null && (mon.width != original.width || mon.height != original.height))
            {
                Console.WriteLine($"[Display] Restoring {mon.name} [PHYSICAL] to {original.width}x{original.height}@{original.refreshRate}Hz");
                DisplayUtil.ForceResolution(mon.name, original.width, original.height, original.refreshRate);
                Thread.Sleep(300);
            }
        }

        Thread.Sleep(500);

        if (physicalMonitors.Count > 0)
        {
            Console.WriteLine($"[Display] Setting {physicalMonitors[0].name} as PRIMARY");
            SetAsPrimaryDisplay(physicalMonitors[0].name);
        }

        // Log final layout
        Console.WriteLine("[Display] Multi-monitor system configured:");
        mons = WgcInterop.ListMonitorsDXGI();
        foreach (var mon in mons)
        {
            var (x, y, w, h, ok) = DisplayUtil.TryGetLayout(mon.name);
            string type = _physicalMonitorNames.Contains(mon.name) ? "PHYSICAL" : "VIRTUAL";
            bool isPrimary = DisplayUtil.IsPrimary(mon.name);
            Console.WriteLine($"[Display]   {mon.name} {w}x{h} at ({x},{y}) [{type}]{(isPrimary ? " [PRIMARY]" : "")}");
        }

        // Set DPI scaling
        Console.WriteLine("[Display] Setting Windows Scale and Layout to 125%...");
        _originalDpiSettings = DpiScalingHelper.GetAllMonitorsDpiInfo();
        if (DpiScalingHelper.SetAllMonitorsDpiScaling(125))
            Console.WriteLine("[Display] Scale and Layout set to 125%");
        else
            Console.WriteLine("[Display] Failed to set Scale and Layout");
    }

    // ================================================================
    // Named Pipe IPC (fast path)
    // ================================================================

    /// <summary>
    /// Send SETDISPLAYCOUNT command via VDD named pipe.
    /// The driver updates XML monitor count and reloads automatically.
    /// Protocol: UTF-16LE text over \\.\pipe\MTTVirtualDisplayPipe
    /// </summary>
    static bool TrySetDisplayCountViaPipe(int count)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", VDD_PIPE_NAME, PipeDirection.InOut);
            pipe.Connect(2000); // Fast fail if pipe doesn't exist

            string command = $"SETDISPLAYCOUNT{count}";
            byte[] cmdBytes = System.Text.Encoding.Unicode.GetBytes(command);
            pipe.Write(cmdBytes, 0, cmdBytes.Length);
            pipe.Flush();

            Console.WriteLine($"[VDD Pipe] Sent: {command}");

            // Try reading response (driver disconnects after processing)
            try
            {
                var readTask = System.Threading.Tasks.Task.Run(() =>
                {
                    byte[] buf = new byte[512];
                    int n = pipe.Read(buf, 0, buf.Length);
                    return n > 0 ? System.Text.Encoding.Unicode.GetString(buf, 0, n).TrimEnd('\0') : "";
                });
                if (readTask.Wait(5000) && !string.IsNullOrEmpty(readTask.Result))
                    Console.WriteLine($"[VDD Pipe] Response: {readTask.Result}");
            }
            catch { /* Response is optional — command was already sent */ }

            return true;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("[VDD Pipe] Not available (driver not running or old version)");
            return false;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[VDD Pipe] IO error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VDD Pipe] Failed: {ex.Message}");
            return false;
        }
    }

    // ================================================================
    // Monitor snapshot
    // ================================================================

    /// <summary>
    /// Classify current monitors into physical/virtual using DisplayUtil.IsVirtualDisplay().
    /// No VDD disable needed — works whether VDD is running or not.
    /// </summary>
    static void SnapshotPhysicalMonitors()
    {
        _physicalMonitorNames.Clear();
        _originalPhysicalMonitors.Clear();

        foreach (var mon in WgcInterop.ListMonitorsDXGI())
        {
            if (!DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon))
            {
                _physicalMonitorNames.Add(mon.name);
                var mode = DisplayUtil.GetCurrentMode(mon.name);
                _originalPhysicalMonitors.Add((mon.name, mode.Width, mode.Height, mode.Frequency));
                Console.WriteLine($"[VDD] Physical monitor: {mon.name} ({mode.Width}x{mode.Height}@{mode.Frequency}Hz)");
            }
        }
    }

    /// <summary>
    /// Poll DXGI until expected monitor count appears (or timeout).
    /// Much faster than blind Thread.Sleep — returns as soon as monitors are ready.
    /// </summary>
    static bool WaitForMonitorCount(int expectedTotal, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var mons = WgcInterop.ListMonitorsDXGI();
            if (mons.Count >= expectedTotal)
            {
                Console.WriteLine($"[VDD] {mons.Count} monitors detected ({sw.ElapsedMilliseconds}ms)");
                return true;
            }
            Thread.Sleep(200);
        }
        var final = WgcInterop.ListMonitorsDXGI();
        Console.WriteLine($"[VDD] Timeout: {final.Count}/{expectedTotal} monitors after {timeoutMs}ms");
        return false;
    }

    // ================================================================
    // Pnputil fallback (slow path)
    // ================================================================

    /// <summary>
    /// Legacy approach: disable VDD → re-enable → wait.
    /// Used when named pipe is unavailable (old VDD version or driver disabled).
    /// </summary>
    static void ToggleVddViaPnputil()
    {
        try
        {
            var adapterId = FindAdapterId();
            if (string.IsNullOrWhiteSpace(adapterId))
            {
                Console.WriteLine("[VDD] Adapter not found. Check if driver is installed.");
                return;
            }

            // Disable if currently enabled (force config reload)
            if (!IsDeviceDisabled(adapterId))
            {
                Console.WriteLine("[VDD] Disabling VDD for config reload...");
                RunPnputil($"/disable-device \"{adapterId}\"");
                Thread.Sleep(1000);
            }

            // Enable with fallback chain
            RunPnputil("/scan-devices");
            Thread.Sleep(300);

            var result = RunPnputil($"/enable-device \"{adapterId}\"");
            if (result.Contains("Failed") || result.Contains("not connected"))
            {
                Console.WriteLine("[VDD] pnputil failed, trying fallbacks...");
                if (!TryEnableWithDevcon(adapterId) && !TryEnableWithSetupAPI(adapterId))
                {
                    Console.WriteLine("[VDD] Could not enable VDD. Please enable manually in Device Manager.");
                    return;
                }
            }

            // Wait for monitors to appear (poll instead of blind sleep)
            WaitForMonitorCount(TARGET_TOTAL_MONITORS, 4000);

            // Enable individual monitor devices if needed
            for (int i = 0; i < TARGET_TOTAL_MONITORS; i++)
            {
                var monitorId = FindDeviceInstanceIdByNameAndClass(MONITOR_NAME, "Monitors");
                if (!string.IsNullOrWhiteSpace(monitorId) && IsDeviceDisabled(monitorId))
                {
                    RunPnputil($"/enable-device \"{monitorId}\"");
                    Thread.Sleep(300);
                }
            }

            TryExtendDesktop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VDD] pnputil fallback failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Find VDD adapter instance ID (cached after first lookup).
    /// </summary>
    static string FindAdapterId()
    {
        if (_cachedAdapterId != null) return _cachedAdapterId;

        var id = FindDeviceInstanceIdByNameAndClass(DRIVER_NAME, "Display adapters");
        if (string.IsNullOrWhiteSpace(id))
            id = FindDeviceInstanceIdByNameAndClass(DRIVER_NAME, null);

        _cachedAdapterId = id ?? "";
        return _cachedAdapterId;
    }

    // ================================================================
    // Device helpers (pnputil text parsing)
    // ================================================================

    static string FindDeviceInstanceIdByNameAndClass(string nameContains, string? className)
    {
        var txt = RunAndRead("pnputil", "/enum-devices");
        string found = ParseForInstanceIdBlock(txt, nameContains, className);
        if (!string.IsNullOrWhiteSpace(found)) return found;

        txt = RunAndRead("pnputil", "/enum-devices /connected");
        return ParseForInstanceIdBlock(txt, nameContains, className);
    }

    static string ParseForInstanceIdBlock(string txt, string nameContains, string? className)
    {
        if (string.IsNullOrEmpty(txt)) return "";
        foreach (var raw in txt.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var blk = raw.Trim();
            if (!string.IsNullOrEmpty(className))
            {
                var iCls = blk.IndexOf("Class Name:", StringComparison.OrdinalIgnoreCase);
                if (iCls >= 0)
                {
                    var line = blk.Substring(iCls).Split('\n').FirstOrDefault() ?? "";
                    if (line.IndexOf(className, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
            }
            if (blk.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

            foreach (var line in blk.Split('\n'))
            {
                var i = line.IndexOf("Instance ID:", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) return line.Substring(i + 12).Trim();
            }
        }
        return "";
    }

    static bool IsDeviceDisabled(string instanceId)
    {
        var txt = RunAndRead("pnputil", "/enum-devices");
        var i = txt.IndexOf(instanceId, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var start = Math.Max(0, i - 200);
        var len = Math.Min(600, txt.Length - start);
        var around = txt.Substring(start, len);
        return around.IndexOf("Status: Disabled", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool TryEnableWithDevcon(string instanceId)
    {
        try
        {
            string[] devconPaths = {
                "devcon.exe",
                @"C:\Program Files (x86)\Windows Kits\10\Tools\x64\devcon.exe",
                @"C:\Program Files\Windows Kits\10\Tools\x64\devcon.exe",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devcon.exe")
            };

            string? devconPath = devconPaths.FirstOrDefault(File.Exists);
            if (devconPath == null)
            {
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

    static bool TryEnableWithSetupAPI(string instanceId)
    {
        try
        {
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

            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);

            if (!string.IsNullOrWhiteSpace(error) && error.Contains("Generic failure"))
            {
                Console.WriteLine("[VDD] SetupAPI: Generic failure");
                return false;
            }

            Thread.Sleep(500);
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

    // ================================================================
    // XML helpers
    // ================================================================

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
            Console.WriteLine($"[VDD] Added resolution {w}x{h}@{hz}");
        }
    }

    // ================================================================
    // Process helpers
    // ================================================================

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
            Thread.Sleep(500);
        }
        catch { }
    }

    // ================================================================
    // Display helpers
    // ================================================================

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

            int result = ChangeDisplaySettingsExA(deviceName, ref dm, IntPtr.Zero,
                CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (result == 0)
            {
                var dmApply = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                ChangeDisplaySettingsExA(null, ref dmApply, IntPtr.Zero, 0, IntPtr.Zero);
                Console.WriteLine($"[Display] {deviceName} set as primary");
            }
            else
            {
                Console.WriteLine($"[Display] Failed to set primary (error: {result})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Display] Error setting primary: {ex.Message}");
        }
    }

    // ================================================================
    // P/Invoke
    // ================================================================

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
