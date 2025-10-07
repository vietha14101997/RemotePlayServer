#nullable enable
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System;
using System.Collections.Generic;

static class DisplayGuard
{
    private static readonly string GuardDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemotePlayServer", "display");
    private static readonly string SnapPath = Path.Combine(GuardDir, "snapshot.json");
    private static readonly string MarkerPath = Path.Combine(GuardDir, "lock.marker");

    private static List<DisplayUtil.DisplayModeSnapshot>? _snapshot;

    // private static readonly string PerMonDpiSnapPath = Path.Combine(GuardDir, "permon_dpi_snapshot.json");
    // private static readonly string PerMonDpiMarkerPath = Path.Combine(GuardDir, "permon_dpi.lock");
    // private static List<DpiPerMonitorUtil.PerMonDpi>? _perMonDpiSnapshot;

    private static readonly string TextSnapPath = System.IO.Path.Combine(GuardDir, "textscale_snapshot.json");
    private static readonly string TextMarkerPath = System.IO.Path.Combine(GuardDir, "textscale.lock");
    private static TextScaleUtil.Snapshot? _textSnap;

    public static bool RestartExplore = false; // cho phép áp ngay mà không cần sign-out

    public static void PrepareAndForceAllTo1366(List<(IntPtr hmon, string name, int w, int h)> mons)
    {
        // 1) Lưu snapshot
        var names = new List<string>();
        foreach (var m in mons) names.Add(m.name);
        _snapshot = DisplayUtil.SnapshotAll(names);
        DisplayUtil.SaveSnapshot(SnapPath, _snapshot);

        // 2) Ghi marker (nếu marker còn tồn tại -> phiên trước không khôi phục được)
        Directory.CreateDirectory(GuardDir);
        File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o"));

        // 3) Tạo RunOnce để nếu máy tắt đột ngột, lần đăng nhập tới sẽ chạy restore
        try
        {
            using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true)
                        ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            rk.SetValue("RemotePlayServerDisplayRestore", $"\"{exe}\" --restore-if-needed");
        }
        catch { /* non-fatal */ }

        // 3.1) Lưu DPI per-monitor hiện tại & ép 125%
        // try
        // {
        //     _perMonDpiSnapshot = DpiPerMonitorUtil.SnapshotAll();
        //     DpiPerMonitorUtil.SaveSnapshot(PerMonDpiSnapPath, _perMonDpiSnapshot);
        //     File.WriteAllText(PerMonDpiMarkerPath, DateTime.UtcNow.ToString("o"));

        //     DpiPerMonitorUtil.SetAllMonitorsScalePercent(125);
        //     if (RestartExplore) DpiUtil.RestartExplorerShell();
        // }
        // catch { /* non-fatal */ }

        try
        {
            _textSnap = TextScaleUtil.Read();
            System.IO.File.WriteAllText(TextSnapPath, System.Text.Json.JsonSerializer.Serialize(_textSnap));
            System.IO.File.WriteAllText(TextMarkerPath, DateTime.UtcNow.ToString("o"));

            TextScaleUtil.SetPercent(125);
            if (RestartExplore) TextScaleUtil.RestartExplorerShell();
        }
        catch { /* best-effort */ }

        // 4) Ép tất cả màn hình về 1366×768@60
        foreach (var m in mons)
        {
            try { DisplayUtil.ForceResolution(m.name, 1366, 768, 60); } catch { }
        }

        // 5) Hook để khi thoát bình thường thì khôi phục + gỡ RunOnce
        AppDomain.CurrentDomain.ProcessExit += (_, __) => RestoreAndCleanup();
        Console.CancelKeyPress += (_, e) => { e.Cancel = false; RestoreAndCleanup(); };
        AppDomain.CurrentDomain.UnhandledException += (_, __) => RestoreAndCleanup();
    }

    public static void RestoreAndCleanup()
    {
        try
        {
            var snaps = _snapshot ?? DisplayUtil.LoadSnapshot(SnapPath);
            if (snaps != null) DisplayUtil.RestoreFromSnapshot(snaps);
        }
        catch { }

        // try
        // {
        //     if (File.Exists(PerMonDpiMarkerPath))
        //     {
        //         var snaps = _perMonDpiSnapshot ?? DpiPerMonitorUtil.LoadSnapshot(PerMonDpiSnapPath);
        //         if (snaps != null) DpiPerMonitorUtil.Restore(snaps);
        //         if (RestartExplore) DpiUtil.RestartExplorerShell();

        //         File.Delete(PerMonDpiMarkerPath);
        //         if (File.Exists(PerMonDpiSnapPath)) File.Delete(PerMonDpiSnapPath);
        //     }
        // }
        // catch { /* best-effort */ }

        try
        {
            if (System.IO.File.Exists(TextMarkerPath))
            {
                var snap = _textSnap;
                if (snap == null && System.IO.File.Exists(TextSnapPath))
                    snap = System.Text.Json.JsonSerializer.Deserialize<TextScaleUtil.Snapshot>(System.IO.File.ReadAllText(TextSnapPath));

                if (snap != null) TextScaleUtil.Restore(snap.Value);
                if (RestartExplore) TextScaleUtil.RestartExplorerShell();

                System.IO.File.Delete(TextMarkerPath);
                if (System.IO.File.Exists(TextSnapPath)) System.IO.File.Delete(TextSnapPath);
            }
        }
        catch { /* best-effort */ }

        try
        {
            // Xoá marker => báo là đã khôi phục OK
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);

            // Gỡ RunOnce (không cần chạy tự phục hồi nữa)
            using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
            rk?.DeleteValue("RemotePlayServerDisplayRestore", false);
        }
        catch { }
    }

    // Dành cho tham số --restore-if-needed (chạy sớm trong Main)
    public static void RestoreIfNeededOnStartup()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return; // phiên trước đã khôi phục OK
            var snaps = DisplayUtil.LoadSnapshot(SnapPath);
            if (snaps != null) DisplayUtil.RestoreFromSnapshot(snaps);
            // Xoá marker sau khôi phục
            File.Delete(MarkerPath);
        }
        catch { /* best-effort */ }

        // try
        // {
        //     if (File.Exists(PerMonDpiMarkerPath))
        //     {
        //         var snaps = DpiPerMonitorUtil.LoadSnapshot(PerMonDpiSnapPath);
        //         if (snaps != null) DpiPerMonitorUtil.Restore(snaps);
        //         if (RestartExplore) DpiUtil.RestartExplorerShell();

        //         File.Delete(PerMonDpiMarkerPath);
        //         if (File.Exists(PerMonDpiSnapPath)) File.Delete(PerMonDpiSnapPath);
        //     }
        // }
        // catch { /* best-effort */ }

        try
        {
            if (System.IO.File.Exists(TextMarkerPath))
            {
                var snap = _textSnap;
                if (snap == null && System.IO.File.Exists(TextSnapPath))
                    snap = System.Text.Json.JsonSerializer.Deserialize<TextScaleUtil.Snapshot>(System.IO.File.ReadAllText(TextSnapPath));

                if (snap != null) TextScaleUtil.Restore(snap.Value);
                if (RestartExplore) TextScaleUtil.RestartExplorerShell();

                System.IO.File.Delete(TextMarkerPath);
                if (System.IO.File.Exists(TextSnapPath)) System.IO.File.Delete(TextSnapPath);
            }
        }
        catch { /* best-effort */ }
    }
}
