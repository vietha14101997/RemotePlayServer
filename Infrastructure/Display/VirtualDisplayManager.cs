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
    private static readonly List<(string name, int width, int height, int refreshRate, int x, int y)> _originalPhysicalMonitors = new();

    // Original DPI settings for restore on shutdown
    private static List<(DpiScalingHelper.LUID adapterId, uint sourceId, DpiScalingHelper.DpiScalingInfo info)>? _originalDpiSettings;

    // Cached adapter instance ID (doesn't change between runs)
    private static string? _cachedAdapterId;

    internal static IReadOnlySet<string> PhysicalMonitorNames => _physicalMonitorNames;

    /// <summary>
    /// Read saved scale % from display-settings.json, default 100 if not found.
    /// </summary>
    static int GetSavedScalePercent()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "display-settings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("scalePercent", out var prop))
                    return prop.GetInt32();
            }
        }
        catch { }
        return 100;
    }

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
        // Cycle: disable → wait → re-enable to force driver reload XML (picks up new resolution)
        if (TrySetDisplayCountViaPipe(0))
        {
            Thread.Sleep(500);
            if (TrySetDisplayCountViaPipe(virtualNeeded))
            {
                Console.WriteLine("[VDD] Display count set via pipe (cycled), waiting for monitors...");
                WaitForMonitorCount(TARGET_TOTAL_MONITORS, timeoutMs: 5000);
                TryExtendDesktop();
                return;
            }
        }
        else if (TrySetDisplayCountViaPipe(virtualNeeded))
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

    /// <summary>
    /// Configure multi-monitor extend desktop with virtual displays.
    /// 4-step pipeline — each step is a single display command + apply + wait.
    /// Step 1: Quantity — VDD count already set by caller, just classify monitors
    /// Step 2: Resolution — ensure virtual monitors match physical resolution
    /// Step 3: Primary — restore original primary display
    /// Step 4: Position — place virtual monitors to the RIGHT of all physical
    ///
    /// <paramref name="requestedScalePercent"/> — when &gt; 0, overrides the
    /// value read from display-settings.json (BUGFIX for 1-monitor ultrawide/
    /// super-ultrawide/bind-mobile not honoring the client's selection).
    /// </summary>
    public static void EnsureExtendDesktopWithVirtual(int requestedScalePercent = 0)
    {
        Console.WriteLine("[Display] ══════ Multi-Monitor Setup Pipeline ══════");

        var mons = WgcInterop.ListMonitorsDXGI();
        if (mons.Count == 0)
        {
            Console.WriteLine("[Display] No monitors detected!");
            return;
        }

        // Classify monitors using snapshot
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

        if (virtualMonitors.Count == 0)
        {
            Console.WriteLine("[Display] No virtual monitors to configure.");
            return;
        }

        // Reference resolution from primary physical monitor
        var primaryOriginal = _originalPhysicalMonitors.FirstOrDefault();
        int targetW = primaryOriginal.width > 0 ? primaryOriginal.width : 1920;
        int targetH = primaryOriginal.height > 0 ? primaryOriginal.height : 1080;
        int targetHz = primaryOriginal.refreshRate > 0 ? primaryOriginal.refreshRate : 60;

        // ── Step 1: Quantity Check (already done by caller — log only) ──
        Console.WriteLine($"[Display] Step 1/4 Quantity: {mons.Count} monitors ({physicalMonitors.Count} physical + {virtualMonitors.Count} virtual) ✓");

        // ── Step 2: Resolution — set virtual monitors to match physical resolution ──
        Console.WriteLine($"[Display] Step 2/4 Resolution: setting virtual to {targetW}x{targetH}@{targetHz}Hz...");
        foreach (var mon in virtualMonitors)
        {
            var current = DisplayUtil.GetCurrentMode(mon.name);
            if (current.Width != targetW || current.Height != targetH || current.Frequency != targetHz)
            {
                Console.WriteLine($"[Display]   {mon.name}: {current.Width}x{current.Height}@{current.Frequency}Hz → {targetW}x{targetH}@{targetHz}Hz");
                DisplayUtil.ForceResolution(mon.name, targetW, targetH, targetHz);
            }
            else
            {
                Console.WriteLine($"[Display]   {mon.name}: already {targetW}x{targetH}@{targetHz}Hz ✓");
            }
        }
        Thread.Sleep(500);

        // ── Step 3: Primary — ensure original primary physical monitor is still primary ──
        string? originalPrimary = null;
        foreach (var orig in _originalPhysicalMonitors)
        {
            if (DisplayUtil.IsPrimary(orig.name))
            {
                originalPrimary = orig.name;
                break;
            }
        }
        // If no physical is primary (VDD stole it), pick first from snapshot
        if (originalPrimary == null && _originalPhysicalMonitors.Count > 0)
            originalPrimary = _originalPhysicalMonitors[0].name;

        if (originalPrimary != null)
        {
            Console.WriteLine($"[Display] Step 3/4 Primary: ensuring {originalPrimary} is main display...");
            SetAsPrimaryDisplay(originalPrimary);
            Thread.Sleep(500);

            // Also restore physical monitors to original resolution (may have been affected)
            foreach (var mon in physicalMonitors)
            {
                var original = _originalPhysicalMonitors.FirstOrDefault(m => m.name == mon.name);
                if (original.name != null)
                {
                    var cur = DisplayUtil.GetCurrentMode(mon.name);
                    if (cur.Width != original.width || cur.Height != original.height)
                    {
                        Console.WriteLine($"[Display]   Restoring {mon.name} resolution: {original.width}x{original.height}@{original.refreshRate}Hz");
                        DisplayUtil.ForceResolution(mon.name, original.width, original.height, original.refreshRate);
                    }
                }
            }
            Thread.Sleep(500);
        }
        else
        {
            Console.WriteLine("[Display] Step 3/4 Primary: no physical primary found, skipping");
        }

        // ── Step 4: Position via CCD API (ChangeDisplaySettingsEx doesn't work for IDD) ──
        Console.WriteLine("[Display] Step 4/4 Position: using CCD API...");

        // Read confirmed physical positions
        int rightmostX = 0;
        int placeY = 0;
        foreach (var mon in physicalMonitors)
        {
            // Use original snapshot positions (more reliable than current which may be shifted)
            var original = _originalPhysicalMonitors.FirstOrDefault(m => m.name == mon.name);
            if (original.name != null)
            {
                Console.WriteLine($"[Display]   Physical {mon.name}: ({original.x},{original.y}) {original.width}x{original.height}");
                if (original.x + original.width > rightmostX)
                {
                    rightmostX = original.x + original.width;
                    placeY = original.y;
                }
            }
        }

        // Build position map: physical at original + virtual to the right
        var positions = new Dictionary<string, (int x, int y)>();
        foreach (var mon in physicalMonitors)
        {
            var original = _originalPhysicalMonitors.FirstOrDefault(m => m.name == mon.name);
            if (original.name != null)
                positions[mon.name] = (original.x, original.y);
        }
        int currentX = rightmostX;
        foreach (var mon in virtualMonitors)
        {
            Console.WriteLine($"[Display]   Virtual {mon.name} → ({currentX}, {placeY})");
            positions[mon.name] = (currentX, placeY);
            currentX += targetW;
        }

        // Apply ALL positions atomically via CCD (legacy ChangeDisplaySettingsEx does NOT work for IDD)
        if (DisplayUtil.SetMonitorPositionsViaCCD(positions))
        {
            Console.WriteLine("[Display]   CCD positioning applied ✓");
        }
        else
        {
            Console.WriteLine("[Display]   CCD positioning failed — positions may be incorrect");
        }
        Thread.Sleep(500);

        // Verify all positions
        foreach (var kvp in positions)
        {
            var (vx, vy, vw, vh, vok) = DisplayUtil.TryGetLayout(kvp.Key);
            bool correct = vok && vx == kvp.Value.x && vy == kvp.Value.y;
            Console.WriteLine($"[Display]   Verify {kvp.Key}: expected ({kvp.Value.x},{kvp.Value.y}) → actual ({vx},{vy}) {(correct ? "✓" : "⚠ MISMATCH")}");
        }

        // ── Final: Log + DPI ──
        Console.WriteLine("[Display] ══════ Final Layout ══════");
        mons = WgcInterop.ListMonitorsDXGI();
        foreach (var mon in mons)
        {
            var (x, y, w, h, ok) = DisplayUtil.TryGetLayout(mon.name);
            string type = _physicalMonitorNames.Contains(mon.name) ? "PHYSICAL" : "VIRTUAL";
            bool isPrimary = DisplayUtil.IsPrimary(mon.name);
            Console.WriteLine($"[Display]   {mon.name} {w}x{h} at ({x},{y}) [{type}]{(isPrimary ? " [PRIMARY]" : "")}");
        }

        int savedScale = requestedScalePercent > 0 ? requestedScalePercent : GetSavedScalePercent();
        Console.WriteLine($"[Display] Setting Windows Scale and Layout to {savedScale}% (source: {(requestedScalePercent > 0 ? "client request" : "display-settings.json")})...");
        _originalDpiSettings = DpiScalingHelper.GetAllMonitorsDpiInfo();
        if (DpiScalingHelper.SetAllMonitorsDpiScaling((uint)savedScale))
            Console.WriteLine($"[Display] Scale set to {savedScale}% ✓");
        else
            Console.WriteLine("[Display] Failed to set scale");
    }

    /// <summary>
    /// Setup a single ultrawide virtual monitor for bind_mobile mode.
    /// 4-step pipeline — each step is a single display command + apply + wait.
    /// Step 1: Create — enable 1 VDD, find the new virtual monitor
    /// Step 2: Resolution — set ultrawide resolution on virtual monitor
    /// Step 3: Primary — switch main display to virtual monitor
    /// Step 4: Show Only — disconnect all physical monitors
    /// </summary>
    /// <returns>The device name of the virtual monitor, or null on failure</returns>
    public static string? SetupUltrawideVirtualMonitor(int width, int height, int refreshRate,
        int requestedScalePercent = 0,
        string settingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml")
    {
        Console.WriteLine("[Bind Mobile] ══════ Virtual Monitor Setup Pipeline ══════");

        // Pre: Ensure VDD is disabled (clean slate) + snapshot physical
        TrySetDisplayCountViaPipe(0);
        Thread.Sleep(500);

        SnapshotPhysicalMonitors();
        int physicalCount = _physicalMonitorNames.Count;
        Console.WriteLine($"[Bind Mobile] Physical: {physicalCount} ({string.Join(", ", _physicalMonitorNames)})");

        try { EnsureResolutionInVddXml(settingsPath, width, height, refreshRate); }
        catch (Exception ex) { Console.WriteLine($"[Bind Mobile] XML resolution edit failed: {ex.Message}"); }

        // ── Step 1: Create — enable 1 VDD ──
        Console.WriteLine($"[Bind Mobile] Step 1/4 Create: enabling 1 virtual monitor...");
        if (!TrySetDisplayCountViaPipe(1))
        {
            Console.WriteLine("[Bind Mobile] Pipe failed, pnputil fallback...");
            try { SetVddMonitorCount(settingsPath, 1); } catch { }
            ToggleVddViaPnputil();
        }
        WaitForMonitorCount(physicalCount + 1, timeoutMs: 5000);
        Thread.Sleep(500);

        string? virtualMonitorName = null;
        foreach (var mon in WgcInterop.ListMonitorsDXGI())
        {
            if (!_physicalMonitorNames.Contains(mon.name))
            {
                virtualMonitorName = mon.name;
                Console.WriteLine($"[Bind Mobile]   Found: {mon.name} ✓");
                break;
            }
        }
        if (virtualMonitorName == null)
        {
            Console.WriteLine("[Bind Mobile] ERROR: Virtual monitor not found!");
            return null;
        }

        // ── Step 2: Resolution — set ultrawide resolution ──
        Console.WriteLine($"[Bind Mobile] Step 2/4 Resolution: {width}x{height}@{refreshRate}Hz...");
        bool resOk = DisplayUtil.ForceResolution(virtualMonitorName, width, height, refreshRate);
        if (!resOk)
        {
            DisplayUtil.ForceResolutionViaModeEnum(virtualMonitorName, width, height, refreshRate);
        }
        Thread.Sleep(500);

        var current = DisplayUtil.GetCurrentMode(virtualMonitorName);
        Console.WriteLine($"[Bind Mobile]   Result: {current.Width}x{current.Height}@{current.Frequency}Hz {(current.Width == width ? "✓" : "⚠")}");

        // ── Step 3: Primary — switch main display to virtual ──
        Console.WriteLine($"[Bind Mobile] Step 3/4 Primary: setting {virtualMonitorName} as main display...");
        SetAsPrimaryDisplay(virtualMonitorName);
        Thread.Sleep(500);

        int savedScale = requestedScalePercent > 0 ? requestedScalePercent : GetSavedScalePercent();
        Console.WriteLine($"[Bind Mobile]   Setting {savedScale}% scale (source: {(requestedScalePercent > 0 ? "client request" : "display-settings.json")})...");
        _originalDpiSettings = DpiScalingHelper.GetAllMonitorsDpiInfo();
        DpiScalingHelper.SetAllMonitorsDpiScaling((uint)savedScale);
        Thread.Sleep(300);

        // ── Step 4: Show Only — disconnect physical monitors ──
        Console.WriteLine($"[Bind Mobile] Step 4/4 Show Only: disconnecting physical monitors...");
        DisplayUtil.SetTopologyShowOnly(virtualMonitorName);
        Thread.Sleep(2000);

        // Verify final resolution (topology change may reset VDD)
        current = DisplayUtil.GetCurrentMode(virtualMonitorName);
        if (current.Width != width || current.Height != height)
        {
            Console.WriteLine($"[Bind Mobile]   Resolution lost after topology change, recovering...");
            DisplayUtil.ForceResolutionViaModeEnum(virtualMonitorName, width, height, refreshRate);
            Thread.Sleep(500);
            current = DisplayUtil.GetCurrentMode(virtualMonitorName);
            if (current.Width != width || current.Height != height)
            {
                Console.WriteLine("[Bind Mobile]   Re-toggling VDD...");
                ToggleVddForModeRefresh();
                Thread.Sleep(1000);
                var newName = FindVirtualMonitorName();
                if (newName != null)
                {
                    virtualMonitorName = newName;
                    DisplayUtil.ForceResolutionViaModeEnum(virtualMonitorName, width, height, refreshRate);
                    DisplayUtil.SetTopologyShowOnly(virtualMonitorName);
                    Thread.Sleep(1000);
                }
            }
        }

        current = DisplayUtil.GetCurrentMode(virtualMonitorName);
        Console.WriteLine($"[Bind Mobile] ══════ Done: {virtualMonitorName} {current.Width}x{current.Height}@{current.Frequency}Hz ══════");
        return virtualMonitorName;
    }

    /// <summary>
    /// Disable all VDD virtual monitors (set count to 0 via pipe).
    /// Used during cleanup to remove virtual displays.
    /// </summary>
    public static void DisableAllVirtualMonitors()
    {
        TrySetDisplayCountViaPipe(0);
        Thread.Sleep(500);
        Console.WriteLine("[VDD] All virtual monitors disabled");
    }

    /// <summary>
    /// Re-toggle VDD (disable → enable) to force driver to reload XML and refresh mode list.
    /// Used after topology change when VDD loses custom resolutions.
    /// </summary>
    public static void ToggleVddForModeRefresh()
    {
        try
        {
            var adapterId = FindAdapterId();
            if (string.IsNullOrWhiteSpace(adapterId))
            {
                Console.WriteLine("[VDD] ToggleForModeRefresh: Adapter not found");
                return;
            }

            Console.WriteLine("[VDD] ToggleForModeRefresh: Disabling VDD...");
            RunPnputil($"/disable-device \"{adapterId}\"");
            Thread.Sleep(1000);

            Console.WriteLine("[VDD] ToggleForModeRefresh: Scanning devices...");
            RunPnputil("/scan-devices");
            Thread.Sleep(300);

            Console.WriteLine("[VDD] ToggleForModeRefresh: Enabling VDD...");
            RunPnputil($"/enable-device \"{adapterId}\"");

            // Wait for monitor to appear
            WaitForMonitorCount(1, timeoutMs: 5000);
            Thread.Sleep(500);

            Console.WriteLine("[VDD] ToggleForModeRefresh: Done");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VDD] ToggleForModeRefresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Find the first virtual monitor name (\\.\DISPLAYx) from DXGI.
    /// Useful after VDD re-toggle when display name may change.
    /// </summary>
    public static string? FindVirtualMonitorName()
    {
        foreach (var mon in WgcInterop.ListMonitorsDXGI())
        {
            if (!_physicalMonitorNames.Contains(mon.name))
            {
                Console.WriteLine($"[VDD] Found virtual monitor: {mon.name}");
                return mon.name;
            }
        }
        return null;
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
    public static void SnapshotPhysicalMonitors()
    {
        _physicalMonitorNames.Clear();
        _originalPhysicalMonitors.Clear();

        foreach (var mon in WgcInterop.ListMonitorsDXGI())
        {
            if (!DisplayUtil.IsVirtualDisplay(mon.name, mon.hmon))
            {
                _physicalMonitorNames.Add(mon.name);
                var mode = DisplayUtil.GetCurrentMode(mon.name);
                var (px, py, _, _, posOk) = DisplayUtil.TryGetLayout(mon.name);
                if (!posOk) { px = 0; py = 0; }
                _originalPhysicalMonitors.Add((mon.name, mode.Width, mode.Height, mode.Frequency, px, py));
                Console.WriteLine($"[VDD] Physical monitor: {mon.name} ({mode.Width}x{mode.Height}@{mode.Frequency}Hz) at ({px},{py})");
            }
        }

        // Share known physical names with DisplayUtil for reliable virtual detection
        DisplayUtil.SetKnownPhysicalNames(_physicalMonitorNames);
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

            // Read current settings to preserve resolution/frequency
            if (!EnumDisplaySettingsExA(deviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref dm, 0))
            {
                Console.WriteLine($"[Display] SetAsPrimaryDisplay: EnumDisplaySettingsEx failed for {deviceName}");
                return;
            }

            // Set position to (0,0) and include resolution fields for a complete mode change
            dm.dmFields = DM_POSITION | 0x00080000 /* DM_PELSWIDTH */ | 0x00100000 /* DM_PELSHEIGHT */ | 0x00400000 /* DM_DISPLAYFREQUENCY */;
            dm.dmPositionX = 0;
            dm.dmPositionY = 0;

            int result = ChangeDisplaySettingsExA(deviceName, ref dm, IntPtr.Zero,
                CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (result == 0)
            {
                var dmApply = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                ChangeDisplaySettingsExA(null, ref dmApply, IntPtr.Zero, 0, IntPtr.Zero);
                Console.WriteLine($"[Display] {deviceName} set as primary at (0,0) {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}Hz");
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

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplaySettingsExA(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, int dwFlags);
}
