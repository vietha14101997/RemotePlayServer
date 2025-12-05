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
        public string? VddInstanceId { get; set; }  // PNPDeviceID
        public bool? VddWasEnabled { get; set; }    // trạng thái driver tại thời điểm chụp
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

    // ==== API được Program.cs gọi ====

    // Gọi sớm nhất có thể (trước các bước 1→6) để chụp trạng thái và tạo marker.
    public static void CaptureSnapshotAtStartup()
    {
        Directory.CreateDirectory(DataDir);

        // Nếu marker cũ còn => có thể phiên trước đã crash -> khôi phục trước rồi chụp mới
        if (File.Exists(SessionMarker) && File.Exists(SnapshotPath))
        {
            try { Console.WriteLine("[Guard] Detected stale session. Restoring previous snapshot before new capture..."); RestoreInternal(readOnly: true, disableVdd: true); }
            catch (Exception ex) { Console.WriteLine("[Guard] Pre-restore failed: " + ex.Message); }
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
                Console.WriteLine($"[Guard] Saved original text scale for {mon.name}: {originalPercent}%");
            }

            // 4) VDD PNP instance & trạng thái
            var vddId = FindPnpInstanceIdByNameContains(DriverNameContains);
            snap.VddInstanceId = vddId;
            snap.VddWasEnabled = string.IsNullOrWhiteSpace(vddId) ? (bool?)null : !IsDeviceDisabled(vddId);

            // Lưu ra file
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(SessionMarker, DateTime.UtcNow.ToString("o"));
            Console.WriteLine("[Guard] Snapshot captured.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Guard] Capture snapshot failed: " + ex.Message);
        }
    }

    // Dùng khi khởi động với flag --restore-if-needed hoặc khi phát hiện marker còn sót
    public static void RestoreIfNeededOnStartup()
    {
        try
        {
            if (File.Exists(SessionMarker) && File.Exists(SnapshotPath))
            {
                Console.WriteLine("[Guard] Restoring previous session state...");
                RestoreInternal(readOnly: false, disableVdd: true);
                File.Delete(SessionMarker);
                Console.WriteLine("[Guard] Restore done.");
            }
        }
        catch (Exception ex) { Console.WriteLine("[Guard] RestoreIfNeeded failed: " + ex.Message); }
    }

    // Gọi ở shutdown bình thường: khôi phục & dọn dẹp marker/snapshot
    public static void RestoreAndCleanup()
    {
        RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(30));
    }

    // Phiên bản với timeout protection để tránh deadlock
    public static void RestoreAndCleanupWithTimeout(TimeSpan timeout)
    {
        Console.WriteLine("[Guard] RestoreAndCleanupWithTimeout called...");
        Console.WriteLine($"[Guard] SnapshotPath exists: {File.Exists(SnapshotPath)}");
        
        // Luôn cố gắng disable VDD trước, bất kể snapshot có tồn tại không
        try
        {
            Console.WriteLine("[Guard] Attempting to disable VDD...");
            ForceDisableVdd();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Guard] Force disable VDD failed: {ex.Message}");
        }

        try
        {
            if (File.Exists(SnapshotPath))
            {
                Console.WriteLine("[Guard] Restoring original state before exit...");

                // Run restore in separate thread with timeout
                using var cts = new CancellationTokenSource(timeout);
                restoreTask = Task.Run(() => RestoreInternalSafe(readOnly: false, disableVdd: false, cts.Token), cts.Token);

                if (restoreTask.Wait(timeout))
                {
                    Console.WriteLine("[Guard] Restore done.");
                }
                else
                {
                    Console.WriteLine("[Guard] Restore timeout.");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[Guard] Restore failed: " + ex.Message); }

        try { if (File.Exists(SessionMarker)) File.Delete(SessionMarker); } catch { }
    }

    /// <summary>
    /// Force disable VDD - gọi trực tiếp không phụ thuộc snapshot
    /// </summary>
    public static void ForceDisableVdd()
    {
        try
        {
            Console.WriteLine("[Guard] ForceDisableVdd: Finding VDD instance...");
            var id = FindPnpInstanceIdByNameContains(DriverNameContains);
            
            if (string.IsNullOrWhiteSpace(id))
            {
                Console.WriteLine("[Guard] ForceDisableVdd: VDD not found - nothing to disable.");
                return;
            }

            Console.WriteLine($"[Guard] ForceDisableVdd: Found VDD ID: {id}");

            bool isDisabled = IsDeviceDisabled(id);
            Console.WriteLine($"[Guard] ForceDisableVdd: VDD is currently {(isDisabled ? "DISABLED" : "ENABLED")}");

            if (isDisabled)
            {
                Console.WriteLine("[Guard] ForceDisableVdd: Already disabled.");
                return;
            }

            // Đảm bảo physical monitor là primary trước khi disable VDD
            var monsNow = WgcInterop.ListMonitorsDXGI();
            var physNames = monsNow
                .Where(m => !DisplayUtil.IsVirtualDisplay(m.name, m.hmon))
                .Select(m => m.name)
                .ToList();

            Console.WriteLine($"[Guard] ForceDisableVdd: Physical monitors: {string.Join(", ", physNames)}");

            if (physNames.Count == 0)
            {
                Console.WriteLine("[Guard] ForceDisableVdd: No physical monitors - SKIP disable to avoid black screen!");
                return;
            }

            // Set physical làm primary
            string primaryName = physNames.OrderBy(n => n).First();
            Console.WriteLine($"[Guard] ForceDisableVdd: Setting {primaryName} as primary...");
            TryMakePrimary(primaryName);
            Thread.Sleep(1000);

            // Disable VDD
            Console.WriteLine($"[Guard] ForceDisableVdd: Disabling VDD...");
            RunPnputil($"/disable-device \"{id}\"");
            Thread.Sleep(1500);

            // Verify
            if (IsDeviceDisabled(id))
            {
                Console.WriteLine("[Guard] ForceDisableVdd: ✓ VDD disabled successfully!");
            }
            else
            {
                Console.WriteLine("[Guard] ForceDisableVdd: ⚠ VDD may still be enabled.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Guard] ForceDisableVdd error: {ex.Message}");
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
        catch (Exception ex) { Console.WriteLine("[Guard] Cannot read snapshot: " + ex.Message); return; }
        if (snap == null) return;

        try
        {
            // 1) Khôi phục độ phân giải with timeout
            if (cancellationToken.IsCancellationRequested) return;
            RestoreMonitorModesSafe(snap);

            // 2) Khôi phục taskbar flag
            if (cancellationToken.IsCancellationRequested) return;
            RestoreTaskbarFlagSafe(snap);

            // 3) Khôi phục Text size
            if (cancellationToken.IsCancellationRequested) return;
            RestoreTextScaleSafe(snap);

            // 3b) Khôi phục per-monitor text scale for virtual monitor
            if (cancellationToken.IsCancellationRequested) return;
            RestorePerMonitorTextScaleSafe(snap);

            // 4) Safe disable VDD (last step, most dangerous)
            if (disableVdd && !cancellationToken.IsCancellationRequested)
            {
                SafeDisableVdd(snap, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Guard] Restore cancelled due to timeout.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Guard] RestoreInternalSafe error: " + ex.Message);
            throw;
        }
        finally
        {
            // Final settling time for all restored settings
            if (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("[Guard] Allowing system to settle after restore...");
                Thread.Sleep(2000); // Give Windows time to apply all registry changes
                Console.WriteLine("[Guard] ✅ All settings restored and system settled.");
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
                Console.WriteLine("[Guard] Monitor modes restored.");
            }
        }
        catch (Exception ex) { Console.WriteLine("[Guard] Restore monitor modes failed: " + ex.Message); }
    }

    static void RestoreTaskbarFlagSafe(Snapshot snap)
    {
        try
        {
            if (snap.MMTaskbarEnabled is int v)
            {
                using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true) ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true);
                rk.SetValue("MMTaskbarEnabled", v, RegistryValueKind.DWord);
                Console.WriteLine($"[Guard] Taskbar multi-monitor flag restored to {v}.");
                Console.WriteLine("[Guard] ℹ️ Taskbar setting will apply automatically (no Explorer restart needed).");
            }
        }
        catch (Exception ex) { Console.WriteLine("[Guard] Taskbar flag restore failed: " + ex.Message); }
    }

    static void RestoreTextScaleSafe(Snapshot snap)
    {
        try
        {
            Console.WriteLine("[Guard] Restoring original text size...");

            // Restore original text scale
            TextScaleUtil.Restore(snap.TextScale);
            Thread.Sleep(500);

            // Verify restore success
            var current = TextScaleUtil.Read();
            bool success = (current.K1 == snap.TextScale.K1 || current.K2 == snap.TextScale.K2) ||
                          (current.K1 == null && current.K2 == null && snap.TextScale.K1 == null && snap.TextScale.K2 == null);

            if (success)
            {
                Console.WriteLine("[Guard] ✓ Text size restored successfully.");
            }
            else
            {
                Console.WriteLine("[Guard] ⚠ Text size may not have been restored properly.");
                Console.WriteLine($"[Guard] Expected: K1={snap.TextScale.K1}, K2={snap.TextScale.K2}");
                Console.WriteLine($"[Guard] Current: K1={current.K1}, K2={current.K2}");

                // Khuyến nghị thủ công thay vì tự động restart
                Console.WriteLine("[Guard] Text size may require Explorer restart to fully restore.");
                Console.WriteLine("[Guard] You can restart Explorer manually if needed:");
                Console.WriteLine("[Guard]   • Press Ctrl+Shift+Right Click on Start -> Restart Explorer");
                Console.WriteLine("[Guard]   • Or run: taskkill /f /im explorer.exe && start explorer.exe");

                // Thử broadcast lại một lần nữa với các messages mạnh hơn
                try
                {
                    Console.WriteLine("[Guard] Attempting enhanced broadcast refresh...");
                    TextScaleUtil.EnhancedBroadcastAndRefresh();

                    // Final verification
                    var current2 = TextScaleUtil.Read();
                    success = (current2.K1 == snap.TextScale.K1 || current2.K2 == snap.TextScale.K2) ||
                              (current2.K1 == null && current2.K2 == null && snap.TextScale.K1 == null && snap.TextScale.K2 == null);
                    Console.WriteLine(success ?
                        "[Guard] ✓ Text size restored after enhanced refresh." :
                        $"[Guard] ⚠ May need manual Explorer restart. Final state: K1={current2.K1}, K2={current2.K2}");
                }
                catch (Exception ex2)
                {
                    Console.WriteLine("[Guard] Enhanced refresh failed: " + ex2.Message);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[Guard] Text size restore failed: " + ex.Message); }
    }

    static void RestorePerMonitorTextScaleSafe(Snapshot snap)
    {
        try
        {
            Console.WriteLine("[Guard] Restoring per-monitor text scale...");

            // Find virtual monitor (last monitor in list)
            var monsNow = WgcInterop.ListMonitorsDXGI();
            int virtMid = monsNow.Count > 0 ? monsNow.Count - 1 : 0;

            if (virtMid >= 0)
            {
                string virtualMonitorName = monsNow[virtMid].name;
                Console.WriteLine($"[Guard] Restoring text scale for virtual monitor: {virtualMonitorName}");

                // Get original percent from snapshot
                if (snap.PerMonitorTextScale.TryGetValue(virtualMonitorName, out int originalPercent))
                {
                    bool success = TextScaleUtil.RestorePerMonitorTextScale(virtualMonitorName, originalPercent);

                    if (success)
                    {
                        Console.WriteLine($"[Guard] ✓ Virtual monitor text scale restored to {originalPercent}%");
                    }
                    else
                    {
                        Console.WriteLine($"[Guard] ⚠ Virtual monitor text scale may need manual restoration");
                        Console.WriteLine($"[Guard] Expected: {originalPercent}%, Current: {TextScaleUtil.GetMonitorDpi(virtualMonitorName) * 100 / 96}%");
                    }
                }
                else
                {
                    Console.WriteLine($"[Guard] No saved text scale for {virtualMonitorName}, assuming 100%");
                    TextScaleUtil.RestorePerMonitorTextScale(virtualMonitorName, 100);
                }
            }

            Console.WriteLine("[Guard] Per-monitor text scale restoration completed.");
        }
        catch (Exception ex) { Console.WriteLine("[Guard] Per-monitor text scale restore failed: " + ex.Message); }
    }

    static void SafeDisableVdd(Snapshot snap, CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine("[Guard] Starting VDD disable process...");
            
            var id = snap.VddInstanceId;
            if (string.IsNullOrWhiteSpace(id)) 
            {
                id = FindPnpInstanceIdByNameContains(DriverNameContains);
                Console.WriteLine($"[Guard] Found VDD instance ID: {id}");
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                Console.WriteLine("[Guard] VDD instance ID not found - skipping disable.");
                return;
            }

            bool isDisabled = IsDeviceDisabled(id);
            Console.WriteLine($"[Guard] VDD current state: {(isDisabled ? "DISABLED" : "ENABLED")}");

            if (isDisabled)
            {
                Console.WriteLine("[Guard] VDD already disabled - nothing to do.");
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

            Console.WriteLine($"[Guard] Physical monitors found: {phys.Count}");

            if (phys.Count == 0)
            {
                Console.WriteLine("[Guard] No physical monitors detected. Skip disabling VDD to avoid black screen.");
                return;
            }

            // Set physical monitor as primary before disabling VDD
            string physPrimary = phys.Select(i => monsNow[i].name)
                                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                     .FirstOrDefault() ?? monsNow[phys[0]].name;

            if (!string.IsNullOrEmpty(physPrimary))
            {
                Console.WriteLine($"[Guard] Setting primary monitor to: {physPrimary}");
                if (TryMakePrimary(physPrimary))
                {
                    Console.WriteLine($"[Guard] ✓ Primary set to {physPrimary}");
                    SleepQuiet(1500);
                }
            }

            if (cancellationToken.IsCancellationRequested) return;

            // Disable VDD
            Console.WriteLine($"[Guard] Disabling VDD: {id}");
            RunPnputilWithTimeout($"/disable-device \"{id}\"", TimeSpan.FromSeconds(15));
            SleepQuiet(1000);
            
            // Verify VDD is disabled
            if (IsDeviceDisabled(id))
            {
                Console.WriteLine("[Guard] ✓ Virtual Display Driver disabled successfully.");
            }
            else
            {
                Console.WriteLine("[Guard] ⚠ VDD may not have been disabled properly.");
            }
        }
        catch (Exception ex) { Console.WriteLine("[Guard] SafeDisableVdd failed: " + ex.Message); }
    }

    static void BasicCleanupOnly()
    {
        try
        {
            // Only clean up basic stuff, skip VDD disable which can hang
            Console.WriteLine("[Guard] Basic cleanup only - skipping VDD disable.");
        }
        catch (Exception ex) { Console.WriteLine("[Guard] Basic cleanup failed: " + ex.Message); }
    }

    static void RunPnputilWithTimeout(string args, TimeSpan timeout)
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

        // Wait with timeout
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            Console.WriteLine("[pnputil] Timeout - killing process");
            try { p.Kill(); } catch { }
            throw new TimeoutException("pnputil operation timed out");
        }

        Console.WriteLine(p.StandardOutput.ReadToEnd());
        var err = p.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine(err);
    }

    // ==== Thao tác chi tiết ====
    static void RestoreInternal(bool readOnly, bool disableVdd)
    {
        if (!File.Exists(SnapshotPath)) return;

        Snapshot? snap = null;
        try { snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath)); }
        catch (Exception ex) { Console.WriteLine("[Guard] Cannot read snapshot: " + ex.Message); return; }
        if (snap == null) return;

        // 1) Khôi phục độ phân giải từng màn TRƯỚC (để đảm bảo có màn vật lý usable)
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
                    }
                }
                Console.WriteLine("[Guard] Monitor modes restored.");
            }
        }
        catch (Exception ex) { Console.WriteLine("[Guard] Restore monitor modes failed: " + ex.Message); }

        // 2) Khôi phục cờ Taskbar multi-monitor
        try
        {
            if (snap.MMTaskbarEnabled is int v)
            {
                using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true)
                             ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true);
                rk.SetValue("MMTaskbarEnabled", v, RegistryValueKind.DWord);
                Console.WriteLine($"[Guard] Taskbar multi-monitor flag restored to {v}.");
                Console.WriteLine("[Guard] ℹ️ Taskbar setting will apply automatically with staggered timing.");
            }
        }
        catch (Exception ex) { Console.WriteLine("[Guard] Taskbar flag restore failed: " + ex.Message); }

        // 3) Khôi phục Text size (global)
        try { TextScaleUtil.Restore(snap.TextScale); Console.WriteLine("[Guard] Text size restored."); }
        catch (Exception ex) { Console.WriteLine("[Guard] Text size restore failed: " + ex.Message); }

        // 4) SAFE DISABLE VDD (nếu có màn vật lý; tránh disable khi màn ảo còn primary)
        if (disableVdd)
        {
            try
            {
                var id = snap.VddInstanceId;
                if (string.IsNullOrWhiteSpace(id)) id = FindPnpInstanceIdByNameContains(DriverNameContains);

                if (!string.IsNullOrWhiteSpace(id) && !IsDeviceDisabled(id))
                {
                    var monsNow = WgcInterop.ListMonitorsDXGI();
                    int virt = -1; var phys = new List<int>();
                    for (int i = 0; i < monsNow.Count; i++)
                    {
                        if (DisplayUtil.IsVirtualDisplay(monsNow[i].name, monsNow[i].hmon)) virt = i;
                        else phys.Add(i);
                    }

                    if (phys.Count == 0)
                    {
                        Console.WriteLine("[Guard] No physical monitors detected. Skip disabling VDD to avoid black screen.");
                    }
                    else
                    {
                        // Đảm bảo 1 màn vật lý là primary trước khi disable VDD
                        // Ưu tiên \\.\DISPLAY1 nếu là vật lý, nếu không chọn phys[0]
                        string physPrimary = phys.Select(i => monsNow[i].name)
                                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                                 .FirstOrDefault() ?? monsNow[phys[0]].name;

                        if (!string.IsNullOrEmpty(physPrimary))
                        {
                            if (TryMakePrimary(physPrimary))
                            {
                                Console.WriteLine($"[Guard] Set primary = {physPrimary}");
                                SleepQuiet(1200); // chờ topology ổn định
                            }
                        }

                        RunPnputil($"/disable-device \"{id}\"");
                        Console.WriteLine("[Guard] Virtual Display Driver disabled safely.");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[Guard] Disable VDD failed: " + ex.Message); }
        }
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
