#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Display;

#if WINDOWS
using Microsoft.Win32;

static class DisplayGuard
{
    // Thư mục lưu snapshot + marker
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RemotePlayServer");
    static readonly string SnapshotPath = Path.Combine(DataDir, "display_snapshot.json");
    static readonly string SessionMarker = Path.Combine(DataDir, "session.lock");

    // Tên driver để tìm PnP Instance
    const string DriverNameContains = "Virtual Display Driver";

    // --- Model ---
    public class Snapshot
    {
        public List<Mon>? Monitors { get; set; }    // danh sách màn hình & độ phân giải
        public int? MMTaskbarEnabled { get; set; }  // 0/1
        public TextScaleUtil.Snapshot TextScale { get; set; }
        public Dictionary<string, int> PerMonitorTextScale { get; set; } = new(); // monitor name -> original percent
        public List<DpiPerMonitorUtil.PerMonDpi>? DpiSnapshot { get; set; }  // Scale and Layout snapshot
        public string? VddInstanceId { get; set; }  // PNPDeviceID
        public bool? VddWasEnabled { get; set; }    // trạng thái driver tại thời điểm chụp

        /// <summary>True if "Show Only" topology was activated during session (ultrawide mode).</summary>
        public bool? ShowOnlyActive { get; set; }
        /// <summary>Device name of the Show Only target monitor.</summary>
        public string? ShowOnlyMonitorName { get; set; }

        /// <summary>True when VDD-only (bind mobile screen) mode is active.</summary>
        public bool? VddOnlyActive { get; set; }
        /// <summary>UTC timestamp when VDD-only mode started (for max duration safety).</summary>
        public DateTime? VddOnlyStartTime { get; set; }
    }

    public class Mon
    {
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        // (tuỳ chọn) vị trí trong desktop topology nếu muốn:
        public int X { get; set; }
        public int Y { get; set; }
        public int Refresh { get; set; } = 60;
        public bool IsVirtual { get; set; }
    }

    // Lock for snapshot file read-modify-write operations (MarkVddOnlyActive, ClearVddOnlyActive, etc.)
    private static readonly object _snapshotLock = new();

    // ==== API được Program.cs gọi ====

    // Gọi sớm nhất có thể (trước các bước 1→6) để chụp trạng thái và tạo marker.
    /// <summary>
    /// Delete old snapshot + session marker, then capture fresh snapshot from current state.
    /// Called from Settings "Save Config as Default" button.
    /// </summary>
    public static void ResetAndCaptureSnapshot()
    {
        try
        {
            if (File.Exists(SnapshotPath)) File.Delete(SnapshotPath);
            if (File.Exists(SessionMarker)) File.Delete(SessionMarker);
            Logger.Info("[Guard] Old snapshot cleared.");
        }
        catch (Exception ex) { Logger.Warn($"[Guard] Failed to clear snapshot: {ex.Message}"); }

        CaptureSnapshotAtStartup();
        Logger.Info("[Guard] Fresh snapshot captured from current display state.");
    }

    public static void CaptureSnapshotAtStartup()
    {
        Directory.CreateDirectory(DataDir);

        // Nếu marker cũ còn => có thể phiên trước đã crash -> khôi phục trước rồi chụp mới
        if (File.Exists(SessionMarker) && File.Exists(SnapshotPath))
        {
            try { Logger.Info("[Guard] Detected stale session. Restoring previous snapshot before new capture..."); RestoreInternal(readOnly: true, disableVdd: true); }
            catch (Exception ex) { Logger.Error("[Guard] Pre-restore failed: " + ex.Message); }
            try { File.Delete(SessionMarker); } catch { }
        }

        var snap = new Snapshot();
        try
        {
            // 1) Monitors & mode
            var mons = WgcInterop.ListMonitorsDXGI();
            var list = new List<Mon>();
            foreach (var m in mons)
            {
                var (x, y, w, h, ok) = DisplayUtil.TryGetLayout(m.name);
                list.Add(new Mon
                {
                    Name = m.name,
                    Width = m.width,
                    Height = m.height,
                    X = ok ? x : 0,
                    Y = ok ? y : 0,
                    Refresh = 60,
                    IsVirtual = DisplayUtil.IsVirtualDisplay(m.name, m.hmon)
                });
            }
            snap.Monitors = list;

            // 2) Taskbar multi-monitor flag
            snap.MMTaskbarEnabled = ReadMMTaskbarEnabled();

            // 3) Text size snapshot (global and per-monitor)
            snap.TextScale = TextScaleUtil.Read();

            // 3b) Per-monitor text scale snapshot
            foreach (var mon in mons)
            {
                int originalPercent = (int)(TextScaleUtil.GetMonitorDpi(mon.name) * 100 / 96);
                snap.PerMonitorTextScale[mon.name] = originalPercent;
                Logger.Info($"[Guard] Saved original text scale for {mon.name}: {originalPercent}%");
            }

            // 3c) Scale and Layout (DPI) snapshot - use API instead of registry
            // DpiPerMonitorUtil.SnapshotAll() reads ALL monitors from registry (including disconnected)
            // DpiScalingHelper.GetAllMonitorsDpiInfo() gets actual DPI for currently active monitors
            var dpiInfoList = DpiScalingHelper.GetAllMonitorsDpiInfo();
            snap.DpiSnapshot = new List<DpiPerMonitorUtil.PerMonDpi>();
            foreach (var (adapterId, sourceId, info) in dpiInfoList)
            {
                if (info.IsValid)
                {
                    // Convert percentage to logPixels: 100% = 96, 125% = 120, 150% = 144
                    int logPixels = (int)(info.Current * 96 / 100);
                    snap.DpiSnapshot.Add(new DpiPerMonitorUtil.PerMonDpi
                    {
                        SubKey = $"LUID_{adapterId.LowPart}_{adapterId.HighPart}_Source_{sourceId}",
                        DpiValue = logPixels
                    });
                    Logger.Info($"[Guard] DPI snapshot: Source {sourceId} = {info.Current}% (logPixels={logPixels})");
                }
            }
            Logger.Info($"[Guard] Saved DPI snapshot for {snap.DpiSnapshot.Count} active monitors (via API)");

            // 4) VDD PNP instance & trạng thái
            var vddId = FindPnpInstanceIdByNameContains(DriverNameContains);
            snap.VddInstanceId = vddId;
            snap.VddWasEnabled = string.IsNullOrWhiteSpace(vddId) ? (bool?)null : !IsDeviceDisabled(vddId);

            // Lưu ra file
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(SessionMarker, DateTime.UtcNow.ToString("o"));
            Logger.Info("[Guard] Snapshot captured.");
        }
        catch (Exception ex)
        {
            Logger.Error("[Guard] Capture snapshot failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Mark that Show Only topology is active in the snapshot file.
    /// Called by PhaseProtocolHandler when ultrawide mode sets Show Only.
    /// This ensures recovery can restore extend topology on crash.
    /// </summary>
    public static void MarkShowOnlyActive(string monitorName)
    {
        lock (_snapshotLock)
        {
            try
            {
                if (!File.Exists(SnapshotPath)) return;
                var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
                if (snap == null) return;

                snap.ShowOnlyActive = true;
                snap.ShowOnlyMonitorName = monitorName;

                File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
                Logger.Info($"[Guard] Marked ShowOnly active: {monitorName}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Guard] MarkShowOnlyActive failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Mark that VDD-only (bind mobile screen) mode is active.
    /// Used by safety layers to detect and recover from crash during VDD-only mode.
    /// </summary>
    public static void MarkVddOnlyActive()
    {
        lock (_snapshotLock)
        {
            try
            {
                if (!File.Exists(SnapshotPath)) return;
                var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
                if (snap == null) return;

                snap.VddOnlyActive = true;
                snap.VddOnlyStartTime = DateTime.UtcNow;

                File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
                Logger.Info("[Guard] Marked VDD-only mode active");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Guard] MarkVddOnlyActive failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Clear VDD-only flag from snapshot (called on normal restore/disconnect).
    /// </summary>
    public static void ClearVddOnlyActive()
    {
        lock (_snapshotLock)
        {
            try
            {
                if (!File.Exists(SnapshotPath)) return;
                var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
                if (snap == null) return;

                snap.VddOnlyActive = false;
                snap.VddOnlyStartTime = null;

                File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
                Logger.Info("[Guard] Cleared VDD-only mode flag");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Guard] ClearVddOnlyActive failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Check if VDD-only mode is active and when it started (for max duration check).
    /// </summary>
    public static (bool Active, DateTime? StartTime) GetVddOnlyState()
    {
        lock (_snapshotLock)
        {
            try
            {
                if (!File.Exists(SnapshotPath)) return (false, null);
                var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
                return (snap?.VddOnlyActive == true, snap?.VddOnlyStartTime);
            }
            catch { return (false, null); }
        }
    }

    // Watchdog process handle (so we can kill it on clean shutdown)
    private static Process? _watchdogProcess;

    /// <summary>
    /// Spawn a watchdog process that monitors the current server PID.
    /// If the server crashes (for any reason), the watchdog restores display settings.
    /// Should be called AFTER display modifications (VDD setup, ShowOnly, etc.).
    /// </summary>
    public static void SpawnWatchdog()
    {
        try
        {
            // Kill any existing watchdog first
            StopWatchdog();

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Logger.Error("[Guard] Cannot spawn watchdog: ProcessPath is null");
                return;
            }

            int myPid = Environment.ProcessId;
            var psi = new ProcessStartInfo(exePath, $"--watchdog-pid={myPid}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            _watchdogProcess = Process.Start(psi);
            if (_watchdogProcess != null)
            {
                Logger.Info($"[Guard] Watchdog spawned (PID={_watchdogProcess.Id}), monitoring server PID={myPid}");
            }
            else
            {
                Logger.Error("[Guard] Failed to spawn watchdog process");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Guard] Watchdog spawn error: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop the watchdog process (called during clean shutdown).
    /// </summary>
    public static void StopWatchdog()
    {
        try
        {
            if (_watchdogProcess != null && !_watchdogProcess.HasExited)
            {
                Logger.Info("[Guard] Stopping watchdog process...");
                _watchdogProcess.Kill();
                _watchdogProcess.Dispose();
            }
        }
        catch { }
        _watchdogProcess = null;
    }

    // Dùng khi khởi động với flag --restore-if-needed hoặc khi phát hiện marker còn sót
    public static void RestoreIfNeededOnStartup()
    {
        try
        {
            if (File.Exists(SessionMarker) && File.Exists(SnapshotPath))
            {
                Logger.Info("[Guard] Restoring previous session state...");
                RestoreInternal(readOnly: false, disableVdd: true);
                File.Delete(SessionMarker);
                Logger.Info("[Guard] Restore done.");
            }
        }
        catch (Exception ex) { Logger.Error("[Guard] RestoreIfNeeded failed: " + ex.Message); }
    }

    // Gọi ở shutdown bình thường: khôi phục & dọn dẹp marker/snapshot
    public static void RestoreAndCleanup()
    {
        RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(30));
    }

    // Phiên bản với timeout protection để tránh deadlock
    public static void RestoreAndCleanupWithTimeout(TimeSpan timeout)
    {
        Logger.Info("[Guard] RestoreAndCleanupWithTimeout called...");

        // Stop watchdog FIRST — so it doesn't also try to restore when this process exits
        StopWatchdog();

        Logger.Info($"[Guard] SnapshotPath exists: {File.Exists(SnapshotPath)}");
        
        // Luôn cố gắng disable VDD trước, bất kể snapshot có tồn tại không
        try
        {
            Logger.Info("[Guard] Attempting to disable VDD...");
            ForceDisableVdd();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Guard] Force disable VDD failed: {ex.Message}");
        }

        try
        {
            if (File.Exists(SnapshotPath))
            {
                Logger.Info("[Guard] Restoring original state before exit...");

                // Run restore in separate thread with timeout
                using var cts = new CancellationTokenSource(timeout);
                restoreTask = Task.Run(() => RestoreInternalSafe(readOnly: false, disableVdd: false, cts.Token), cts.Token);

                if (restoreTask.Wait(timeout))
                {
                    Logger.Info("[Guard] Restore done.");
                }
                else
                {
                    Logger.Info("[Guard] Restore timeout.");
                }
            }
        }
        catch (Exception ex) { Logger.Error("[Guard] Restore failed: " + ex.Message); }

        try { if (File.Exists(SessionMarker)) File.Delete(SessionMarker); } catch { }
    }

    /// <summary>
    /// Force disable VDD - gọi trực tiếp không phụ thuộc snapshot
    /// </summary>
    public static void ForceDisableVdd()
    {
        try
        {
            Logger.Info("[Guard] ForceDisableVdd: Finding VDD instance...");
            var id = FindPnpInstanceIdByNameContains(DriverNameContains);
            
            if (string.IsNullOrWhiteSpace(id))
            {
                Logger.Info("[Guard] ForceDisableVdd: VDD not found - nothing to disable.");
                return;
            }

            Logger.Info($"[Guard] ForceDisableVdd: Found VDD ID: {id}");

            bool isDisabled = IsDeviceDisabled(id);
            Logger.Info($"[Guard] ForceDisableVdd: VDD is currently {(isDisabled ? "DISABLED" : "ENABLED")}");

            if (isDisabled)
            {
                Logger.Info("[Guard] ForceDisableVdd: Already disabled.");
                return;
            }

            // Đảm bảo physical monitor là primary trước khi disable VDD
            var monsNow = WgcInterop.ListMonitorsDXGI();
            var physNames = monsNow
                .Where(m => !DisplayUtil.IsVirtualDisplay(m.name, m.hmon))
                .Select(m => m.name)
                .ToList();

            Logger.Info($"[Guard] ForceDisableVdd: Physical monitors: {string.Join(", ", physNames)}");

            bool wasPhysicalMonitorsPresent = false;
            try
            {
                if (File.Exists(SnapshotPath))
                {
                    var snapStr = File.ReadAllText(SnapshotPath);
                    var snap = JsonSerializer.Deserialize<Snapshot>(snapStr);
                    if (snap?.Monitors != null && snap.Monitors.Any(m => !m.IsVirtual))
                    {
                        wasPhysicalMonitorsPresent = true;
                    }
                }
            }
            catch { }

            if (physNames.Count == 0 && !wasPhysicalMonitorsPresent)
            {
                Logger.Info("[Guard] ForceDisableVdd: No physical monitors currently or in snapshot. SKIP disable to avoid black screen!");
                return;
            }

            if (physNames.Count == 0 && wasPhysicalMonitorsPresent)
            {
                Logger.Info("[Guard] ForceDisableVdd: 0 physical monitors currently, but snapshot confirms they exist. Proceeding to disable VDD to restore physical screens.");
            }

            // Set physical làm primary
            string? primaryName = physNames.OrderBy(n => n).FirstOrDefault();
            if (!string.IsNullOrEmpty(primaryName))
            {
                Logger.Info($"[Guard] ForceDisableVdd: Setting {primaryName} as primary...");
                TryMakePrimary(primaryName);
                Thread.Sleep(1000);
            }
            else
            {
                Logger.Info("[Guard] ForceDisableVdd: No physical monitors to set as primary. Skipping TryMakePrimary.");
            }

            // Disable VDD
            Logger.Info($"[Guard] ForceDisableVdd: Disabling VDD...");
            RunPnputil($"/disable-device \"{id}\"");
            Thread.Sleep(1500);

            // Verify
            if (IsDeviceDisabled(id))
            {
                Logger.Info("[Guard] ForceDisableVdd: ✓ VDD disabled successfully!");
            }
            else
            {
                Logger.Info("[Guard] ForceDisableVdd: ⚠ VDD may still be enabled.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Guard] ForceDisableVdd error: {ex.Message}");
        }
    }

    static Task restoreTask = Task.CompletedTask;

    static void RestoreInternalSafe(bool readOnly, bool disableVdd, CancellationToken cancellationToken)
    {
        if (!File.Exists(SnapshotPath)) return;

        Snapshot? snap = null;
        try
        {
            if (cancellationToken.IsCancellationRequested) return;
            snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
        }
        catch (Exception ex) { Logger.Error("[Guard] Cannot read snapshot: " + ex.Message); return; }
        if (snap == null) return;

        try
        {
            // 0) Restore extend topology if Show Only was active (ultrawide mode)
            // Must be done FIRST so physical monitors are re-attached before other steps
            if (snap.ShowOnlyActive == true && snap.Monitors != null)
            {
                if (cancellationToken.IsCancellationRequested) return;
                Logger.Info("[Guard] Step 0: Restoring extend topology (was Show Only)...");
                try
                {
                    // Convert snapshot monitors to DisplayModeSnapshot for RestoreExtendTopology
                    var physicalSnapshots = snap.Monitors
                        .Where(m => !m.IsVirtual)
                        .Select(m => new DisplayUtil.DisplayModeSnapshot
                        {
                            DeviceName = m.Name,
                            X = m.X,
                            Y = m.Y,
                            Width = m.Width,
                            Height = m.Height,
                            Frequency = Math.Max(30, m.Refresh)
                        })
                        .ToList();

                    if (physicalSnapshots.Count > 0)
                    {
                        DisplayUtil.RestoreExtendTopology(physicalSnapshots);
                        Thread.Sleep(2000); // Wait for Windows to re-attach displays
                        Logger.Info("[Guard] Extend topology restored from Show Only.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Guard] Restore extend topology failed: {ex.Message}");
                }
            }

            // 1) Khôi phục Scale and Layout (DPI) - CHỈ cần restore cái này
            // Không restore TextScale vì chúng ta không thay đổi nó khi connect
            // TextScaleFactor (Make text bigger) khác với Scale and Layout (DpiValue)
            if (cancellationToken.IsCancellationRequested) return;
            Logger.Info("[Guard] Step 1: Restoring Scale and Layout (DPI)...");
            RestoreDpiSafe(snap);

            // 2) Khôi phục độ phân giải
            if (cancellationToken.IsCancellationRequested) return;
            Logger.Info("[Guard] Step 2: Restoring monitor modes...");
            RestoreMonitorModesSafe(snap);

            // 3) Khôi phục taskbar flag
            if (cancellationToken.IsCancellationRequested) return;
            Logger.Info("[Guard] Step 3: Restoring taskbar flag...");
            RestoreTaskbarFlagSafe(snap);

            // 4) Safe disable VDD (last step, most dangerous)
            if (disableVdd && !cancellationToken.IsCancellationRequested)
            {
                Logger.Info("[Guard] Step 4: Disabling VDD...");
                SafeDisableVdd(snap, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("[Guard] Restore cancelled due to timeout.");
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("[Guard] RestoreInternalSafe error: " + ex.Message);
            throw;
        }
        finally
        {
            // Final settling time for all restored settings
            if (!cancellationToken.IsCancellationRequested)
            {
                Logger.Info("[Guard] Allowing system to settle after restore...");
                Thread.Sleep(2000); // Give Windows time to apply all registry changes
                Logger.Info("[Guard] ✅ All settings restored and system settled.");
            }
        }
    }

    static void RestoreMonitorModesSafe(Snapshot snap)
    {
        try
        {
            if (snap.Monitors != null)
            {
                var current = WgcInterop.ListMonitorsDXGI();
                foreach (var m in snap.Monitors)
                {
                    var cur = current.FirstOrDefault(c => string.Equals(c.name, m.Name, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(cur.name) && m.Width > 0 && m.Height > 0)
                    {
                        DisplayUtil.ForceResolution(cur.name, m.Width, m.Height, Math.Max(30, m.Refresh));
                        Thread.Sleep(200); // Small delay between resolution changes
                    }
                }
                Logger.Info("[Guard] Monitor modes restored.");
            }
        }
        catch (Exception ex) { Logger.Error("[Guard] Restore monitor modes failed: " + ex.Message); }
    }

    static void RestoreTaskbarFlagSafe(Snapshot snap)
    {
        try
        {
            if (snap.MMTaskbarEnabled is int v)
            {
                using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true) ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true);
                rk.SetValue("MMTaskbarEnabled", v, RegistryValueKind.DWord);
                Logger.Info($"[Guard] Taskbar multi-monitor flag restored to {v}.");
                Logger.Info("[Guard] ℹ️ Taskbar setting will apply automatically (no Explorer restart needed).");
            }
        }
        catch (Exception ex) { Logger.Error("[Guard] Taskbar flag restore failed: " + ex.Message); }
    }

    static void RestoreDpiSafe(Snapshot snap)
    {
        try
        {
            if (snap.DpiSnapshot != null && snap.DpiSnapshot.Count > 0)
            {
                Logger.Info("[Guard] Restoring Scale and Layout (DPI)...");

                // Prefer user-saved scale from display-settings.json over snapshot
                int percent = 100;
                try
                {
                    var configPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "display-settings.json");
                    if (File.Exists(configPath))
                    {
                        var json = File.ReadAllText(configPath);
                        var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("scalePercent", out var prop))
                        {
                            percent = prop.GetInt32();
                            Logger.Info($"[Guard] Using saved scale from display-settings.json: {percent}%");
                        }
                    }
                }
                catch { }

                // Fallback to snapshot if no saved config
                if (percent == 100)
                {
                    var firstDpi = snap.DpiSnapshot.FirstOrDefault(s => s.DpiValue.HasValue);
                    if (firstDpi.DpiValue.HasValue)
                    {
                        percent = firstDpi.DpiValue.Value * 100 / 96;
                    }
                }

                Logger.Info($"[Guard] Restoring DPI to {percent}%");
                bool success = DpiScalingHelper.SetAllMonitorsDpiScaling((uint)percent);
                if (success)
                {
                    Logger.Info("[Guard] ✓ Scale and Layout restored via API.");
                }
                else
                {
                    Logger.Error("[Guard] API failed, falling back to registry method...");
                    DpiPerMonitorUtil.Restore(snap.DpiSnapshot);
                    Logger.Info("[Guard] ✓ Scale and Layout restored via registry.");
                }
            }
            else
            {
                Logger.Info("[Guard] No DPI snapshot available, skipping.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Guard] DPI restore failed: {ex.Message}");
        }
    }

    static void RestoreTextScaleSafe(Snapshot snap)
    {
        try
        {
            Logger.Info("[Guard] Restoring original text size...");

            // Restore original text scale
            TextScaleUtil.Restore(snap.TextScale);
            Thread.Sleep(500);

            // Verify restore success
            var current = TextScaleUtil.Read();
            bool success = (current.K1 == snap.TextScale.K1 || current.K2 == snap.TextScale.K2) ||
                          (current.K1 == null && current.K2 == null && snap.TextScale.K1 == null && snap.TextScale.K2 == null);

            if (success)
            {
                Logger.Info("[Guard] ✓ Text size restored successfully.");
            }
            else
            {
                Logger.Info("[Guard] ⚠ Text size may not have been restored properly.");
                Logger.Info($"[Guard] Expected: K1={snap.TextScale.K1}, K2={snap.TextScale.K2}");
                Logger.Info($"[Guard] Current: K1={current.K1}, K2={current.K2}");

                // Khuyến nghị thủ công thay vì tự động restart
                Logger.Info("[Guard] Text size may require Explorer restart to fully restore.");
                Logger.Info("[Guard] You can restart Explorer manually if needed:");
                Logger.Info("[Guard]   • Press Ctrl+Shift+Right Click on Start -> Restart Explorer");
                Logger.Info("[Guard]   • Or run: taskkill /f /im explorer.exe && start explorer.exe");

                // Thử broadcast lại một lần nữa với các messages mạnh hơn
                try
                {
                    Logger.Info("[Guard] Attempting enhanced broadcast refresh...");
                    TextScaleUtil.EnhancedBroadcastAndRefresh();

                    // Final verification
                    var current2 = TextScaleUtil.Read();
                    success = (current2.K1 == snap.TextScale.K1 || current2.K2 == snap.TextScale.K2) ||
                              (current2.K1 == null && current2.K2 == null && snap.TextScale.K1 == null && snap.TextScale.K2 == null);
                    Logger.Info(success ?
                        "[Guard] ✓ Text size restored after enhanced refresh." :
                        $"[Guard] ⚠ May need manual Explorer restart. Final state: K1={current2.K1}, K2={current2.K2}");
                }
                catch (Exception ex2)
                {
                    Logger.Error("[Guard] Enhanced refresh failed: " + ex2.Message);
                }
            }
        }
        catch (Exception ex) { Logger.Error("[Guard] Text size restore failed: " + ex.Message); }
    }

    static void RestorePerMonitorTextScaleSafe(Snapshot snap)
    {
        try
        {
            Logger.Info("[Guard] Restoring per-monitor text scale...");

            // Find virtual monitor (last monitor in list)
            var monsNow = WgcInterop.ListMonitorsDXGI();
            int virtMid = monsNow.Count > 0 ? monsNow.Count - 1 : 0;

            if (virtMid >= 0)
            {
                string virtualMonitorName = monsNow[virtMid].name;
                Logger.Info($"[Guard] Restoring text scale for virtual monitor: {virtualMonitorName}");

                // Get original percent from snapshot
                if (snap.PerMonitorTextScale.TryGetValue(virtualMonitorName, out int originalPercent))
                {
                    bool success = TextScaleUtil.RestorePerMonitorTextScale(virtualMonitorName, originalPercent);

                    if (success)
                    {
                        Logger.Info($"[Guard] ✓ Virtual monitor text scale restored to {originalPercent}%");
                    }
                    else
                    {
                        Logger.Info($"[Guard] ⚠ Virtual monitor text scale may need manual restoration");
                        Logger.Info($"[Guard] Expected: {originalPercent}%, Current: {TextScaleUtil.GetMonitorDpi(virtualMonitorName) * 100 / 96}%");
                    }
                }
                else
                {
                    Logger.Info($"[Guard] No saved text scale for {virtualMonitorName}, assuming 100%");
                    TextScaleUtil.RestorePerMonitorTextScale(virtualMonitorName, 100);
                }
            }

            Logger.Info("[Guard] Per-monitor text scale restoration completed.");
        }
        catch (Exception ex) { Logger.Error("[Guard] Per-monitor text scale restore failed: " + ex.Message); }
    }

    static void SafeDisableVdd(Snapshot snap, CancellationToken cancellationToken)
    {
        try
        {
            Logger.Info("[Guard] Starting VDD disable process...");
            
            var id = snap.VddInstanceId;
            if (string.IsNullOrWhiteSpace(id)) 
            {
                id = FindPnpInstanceIdByNameContains(DriverNameContains);
                Logger.Info($"[Guard] Found VDD instance ID: {id}");
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                Logger.Info("[Guard] VDD instance ID not found - skipping disable.");
                return;
            }

            bool isDisabled = IsDeviceDisabled(id);
            Logger.Info($"[Guard] VDD current state: {(isDisabled ? "DISABLED" : "ENABLED")}");

            if (isDisabled)
            {
                Logger.Info("[Guard] VDD already disabled - nothing to do.");
                return;
            }

            if (cancellationToken.IsCancellationRequested) return;

            var monsNow = WgcInterop.ListMonitorsDXGI();
            var phys = new List<int>();
            for (int i = 0; i < monsNow.Count; i++)
            {
                if (!DisplayUtil.IsVirtualDisplay(monsNow[i].name, monsNow[i].hmon))
                    phys.Add(i);
            }

            Logger.Info($"[Guard] Physical monitors found: {phys.Count}");

            bool wasPhysicalMonitorsPresent = false;
            if (snap.Monitors != null && snap.Monitors.Any(m => !m.IsVirtual))
            {
                wasPhysicalMonitorsPresent = true;
            }

            if (phys.Count == 0 && !wasPhysicalMonitorsPresent)
            {
                Logger.Info("[Guard] SafeDisableVdd: No physical monitors currently or in snapshot. Skip disabling VDD to avoid black screen.");
                return;
            }

            if (phys.Count == 0 && wasPhysicalMonitorsPresent)
            {
                Logger.Info("[Guard] SafeDisableVdd: 0 physical monitors currently, but snapshot confirms they exist. Proceeding to disable VDD.");
            }

            // Set physical monitor as primary before disabling VDD
            string? physPrimary = phys.Select(i => monsNow[i].name)
                                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                     .FirstOrDefault();

            if (!string.IsNullOrEmpty(physPrimary))
            {
                Logger.Info($"[Guard] Setting primary monitor to: {physPrimary}");
                if (TryMakePrimary(physPrimary))
                {
                    Logger.Info($"[Guard] ✓ Primary set to {physPrimary}");
                    SleepQuiet(1500);
                }
            }

            if (cancellationToken.IsCancellationRequested) return;

            // Disable VDD
            Logger.Info($"[Guard] Disabling VDD: {id}");
            RunPnputilWithTimeout($"/disable-device \"{id}\"", TimeSpan.FromSeconds(15));
            SleepQuiet(1000);
            
            // Verify VDD is disabled
            if (IsDeviceDisabled(id))
            {
                Logger.Info("[Guard] ✓ Virtual Display Driver disabled successfully.");
            }
            else
            {
                Logger.Info("[Guard] ⚠ VDD may not have been disabled properly.");
            }
        }
        catch (Exception ex) { Logger.Error("[Guard] SafeDisableVdd failed: " + ex.Message); }
    }

    static void BasicCleanupOnly()
    {
        try
        {
            // Only clean up basic stuff, skip VDD disable which can hang
            Logger.Info("[Guard] Basic cleanup only - skipping VDD disable.");
        }
        catch (Exception ex) { Logger.Error("[Guard] Basic cleanup failed: " + ex.Message); }
    }

    static void RunPnputilWithTimeout(string args, TimeSpan timeout)
    {
        Logger.Info("[pnputil] " + args);
        var psi = new ProcessStartInfo("pnputil", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Verb = "runas"
        };

        using var p = Process.Start(psi)!;

        // Wait with timeout
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            Logger.Info("[pnputil] Timeout - killing process");
            try { p.Kill(); } catch { }
            throw new TimeoutException("pnputil operation timed out");
        }

        Logger.Info(p.StandardOutput.ReadToEnd());
        var err = p.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(err)) Logger.Info(err);
    }

    // ==== Thao tác chi tiết ====
    static void RestoreInternal(bool readOnly, bool disableVdd)
    {
        if (!File.Exists(SnapshotPath)) return;

        Snapshot? snap = null;
        try { snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath)); }
        catch (Exception ex) { Logger.Error("[Guard] Cannot read snapshot: " + ex.Message); return; }
        if (snap == null) return;

        // Use CancellationToken.None for non-graceful startup/crash recovery
        var ct = CancellationToken.None;

        // 0) Restore extend topology if Show Only or VDD-only was active
        if ((snap.ShowOnlyActive == true || snap.VddOnlyActive == true) && snap.Monitors != null)
        {
            Logger.Info("[Guard] Step 0: Restoring extend topology (was Show Only)...");
            try
            {
                var physicalSnapshots = snap.Monitors
                    .Where(m => !m.IsVirtual)
                    .Select(m => new DisplayUtil.DisplayModeSnapshot
                    {
                        DeviceName = m.Name,
                        X = m.X,
                        Y = m.Y,
                        Width = m.Width,
                        Height = m.Height,
                        Frequency = Math.Max(30, m.Refresh)
                    })
                    .ToList();

                if (physicalSnapshots.Count > 0)
                {
                    DisplayUtil.RestoreExtendTopology(physicalSnapshots);
                    
                    // Poll for physical monitor detection up to 5 seconds
                    for (int i = 0; i < 10; i++)
                    {
                        Thread.Sleep(500);
                        var curMons = WgcInterop.ListMonitorsDXGI();
                        if (curMons.Any(m => !DisplayUtil.IsVirtualDisplay(m.name, m.hmon)))
                        {
                            Logger.Info("[Guard] Physical monitor detected after topology restore.");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"[Guard] Restore extend topology failed: {ex.Message}"); }
        }

        // 1) Restore Scale and Layout (DPI) - CRITICAL: Was missing in old RestoreInternal
        Logger.Info("[Guard] Step 1: Restoring Scale and Layout (DPI)...");
        RestoreDpiSafe(snap);

        // 2) Restore monitor resolution
        Logger.Info("[Guard] Step 2: Restoring monitor modes...");
        RestoreMonitorModesSafe(snap);

        // 3) Restore taskbar flag
        Logger.Info("[Guard] Step 3: Restoring taskbar flag...");
        RestoreTaskbarFlagSafe(snap);

        // 4) Safe disable VDD
        if (disableVdd)
        {
            Logger.Info("[Guard] Step 4: Disabling VDD...");
            SafeDisableVdd(snap, ct);
        }

        // Extra: Restore text scale if it was changed (though usually we don't change it on connect)
        RestoreTextScaleSafe(snap);
    }

    // ==== Helpers: Taskbar, Explorer, pnputil, v.v. ====
    static int? ReadMMTaskbarEnabled()
    {
        try
        {
            using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false);
            var v = rk?.GetValue("MMTaskbarEnabled");
            if (v is int iv) return iv;
        }
        catch { }
        return null;
    }

    static string FindPnpInstanceIdByNameContains(string nameContains)
    {
        try
        {
            var txt = RunAndRead("pnputil", "/enum-devices /connected");
            string found = "";
            foreach (var blk in txt.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (blk.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (var line in blk.Split('\n'))
                    {
                        var i = line.IndexOf("Instance ID:", StringComparison.OrdinalIgnoreCase);
                        if (i >= 0) { found = line.Substring(i + 12).Trim(); break; }
                    }
                }
                if (!string.IsNullOrWhiteSpace(found)) break;
            }
            return found;
        }
        catch { return ""; }
    }

    static bool IsDeviceDisabled(string instanceId)
    {
        var txt = RunAndRead("pnputil", "/enum-devices /connected");
        var i = txt.IndexOf(instanceId, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var around = txt.Substring(Math.Max(0, i - 200), Math.Min(400, txt.Length - Math.Max(0, i - 200)));
        return around.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0;
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
        Logger.Info("[pnputil] " + args);
        var psi = new ProcessStartInfo("pnputil", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Verb = "runas"
        };
        using var p = Process.Start(psi)!;
        Logger.Info(p.StandardOutput.ReadToEnd());
        var err = p.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(err)) Logger.Info(err);
        p.WaitForExit();
    }

    // ===== Helpers to switch primary display safely =====
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    const int DM_POSITION = 0x00000020;
    const int DM_PELSWIDTH = 0x00080000;
    const int DM_PELSHEIGHT = 0x00100000;
    const int DM_DISPLAYFREQUENCY = 0x00400000;
    const int CDS_UPDATEREGISTRY = 0x00000001;
    const int CDS_NORESET = 0x10000000;
    const int CDS_SET_PRIMARY = 0x00000010;

    static bool TryMakePrimary(string displayName) // "\\.\DISPLAY1"
    {
        try
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmFields = DM_POSITION };
            // Set primary: position 0,0 + CDS_SET_PRIMARY
            dm.dmPositionX = 0; dm.dmPositionY = 0;
            int r = ChangeDisplaySettingsEx(displayName, ref dm, IntPtr.Zero, CDS_SET_PRIMARY | CDS_NORESET, IntPtr.Zero);
            if (r != 0) return false;

            // Áp thay đổi
            dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            r = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            return r == 0;
        }
        catch { return false; }
    }

    static void SleepQuiet(int ms) { try { System.Threading.Thread.Sleep(ms); } catch { } }
}
#endif
