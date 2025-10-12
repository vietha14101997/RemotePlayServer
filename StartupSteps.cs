#nullable enable
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management; // System.Management nuget (Windows)
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

static class StartupSteps
{
    // ===== BƯỚC 1: đảm bảo <resolution>4802x1802@30 trong vdd_settings.xml + toggle driver =====
    public static bool EnsureVddResolutionThenToggleDriver(string settingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml",
                                                          string driverNameContains = "Virtual Display Driver")
    {
        bool edited = EnsureResolutionInVddXml(settingsPath, 4802, 1802, 30);
        if (!edited) Console.WriteLine("[VDD] Resolution already exists or file missing.");
        else Console.WriteLine("[VDD] Resolution 4802x1802@30 added.");

        // Toggle driver qua pnputil (admin). Nếu đang Disable thì chỉ Enable.
        try
        {
            var instId = FindPnpInstanceIdByNameContains(driverNameContains);
            if (string.IsNullOrWhiteSpace(instId))
            {
                Console.WriteLine("[VDD] Could not find device instance id by name. Please toggle in Device Manager manually.");
                return edited;
            }

            // Kiểm tra trạng thái hiện tại
            bool isDisabled = IsDeviceDisabled(instId);

            // Nếu đang Disable => Enable; nếu đang Enable => Disable rồi Enable
            if (isDisabled)
            {
                RunPnputil($"/enable-device \"{instId}\"");
                Console.WriteLine("[VDD] Enabled device.");
            }
            else
            {
                RunPnputil($"/disable-device \"{instId}\"");
                RunPnputil($"/enable-device \"{instId}\"");
                Console.WriteLine("[VDD] Toggled device (disable -> enable).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[VDD] Toggle device via pnputil failed: " + ex.Message);
            Console.WriteLine("      TIP: Disable/Enable thủ công: Computer Management > Device Manager > Display adapters > Virtual Display Driver.");
        }

        return edited;
    }

    static bool EnsureResolutionInVddXml(string path, int w, int h, int hz)
    {
        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine("[VDD] File not found: " + path);
                return false;
            }

            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var root = doc.Root ?? new XElement("vdd_settings");
            var resRoot = root.Element("resolutions");
            if (resRoot == null) { resRoot = new XElement("resolutions"); root.Add(resRoot); }

            bool exists = resRoot.Elements("resolution")
                .Any(r => (int?)r.Element("width") == w && (int?)r.Element("height") == h && (int?)r.Element("refresh_rate") == hz);

            if (!exists)
            {
                resRoot.Add(new XElement("resolution",
                    new XElement("width", w),
                    new XElement("height", h),
                    new XElement("refresh_rate", hz)
                ));
                doc.Save(path);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[VDD] XML edit failed: " + ex.Message);
            return false;
        }
    }

    static string FindPnpInstanceIdByNameContains(string nameContains)
    {
        using var s = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity");
        foreach (ManagementObject o in s.Get())
        {
            string? name = o["Name"] as string;
            if (!string.IsNullOrWhiteSpace(name) && name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string? id = o["PNPDeviceID"] as string;
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        }
        return "";
    }

    static bool IsDeviceDisabled(string instanceId)
    {
        // Đơn giản: gọi pnputil /enum-devices và nhìn trạng thái
        string outp = RunAndRead("pnputil", "/enum-devices /connected");
        var lines = (outp ?? "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf(instanceId, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Dòng tiếp/tiếp nữa sẽ có trạng thái
                // Không có chuẩn 100%, nhưng nhiều build sẽ có "Status: Disabled"
                for (int k = 0; k < 4 && i + k < lines.Length; k++)
                {
                    if (lines[i + k].IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
        }
        return false;
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
            Verb = "runas" // gợi ý admin
        };
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine(outp.Trim());
        if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine(err.Trim());
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
        string text = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
        p.WaitForExit(4000);
        return text;
    }

    // ===== BƯỚC 2: tắt "Show my taskbar on all displays" =====
    // HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\MMTaskbarEnabled = 0
    public static void DisableMultiMonitorTaskbarAndRestartExplorer()
    {
        try
        {
            using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true)
                        ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true);
            rk.SetValue("MMTaskbarEnabled", 0, RegistryValueKind.DWord);
            Console.WriteLine("[Taskbar] MMTaskbarEnabled=0 set.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Taskbar] Failed set registry: " + ex.Message);
        }

        // restart explorer để áp tức thì (best-effort)
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) p.Kill();
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            Console.WriteLine("[Taskbar] Explorer restarted.");
        }
        catch { /* best-effort */ }
    }

    // ===== BƯỚC 3: đặt độ phân giải 4802x1802 cho màn ảo =====
    public static void ForceVirtualTo4802x1802()
    {
        var mons = WgcInterop.ListMonitorsDXGI(); // (hmon,name,w,h)
        int idx = PickVirtualDisplay(mons);
        if (idx < 0 && mons.Count > 0) idx = mons.Count - 1;

        if (idx >= 0)
        {
            Console.WriteLine($"[Display] Forcing {mons[idx].name} -> 4802x1802@30");
            bool ok = DisplayUtil.ForceResolution(mons[idx].name, 4802, 1802, 30);
            Console.WriteLine(ok ? "[Display] OK" : "[Display] Failed to set mode");
        }
        else
        {
            Console.WriteLine("[Display] No monitor found to set 4802x1802.");
        }
    }

    static int PickVirtualDisplay(List<(IntPtr hmon, string name, int w, int h)> mons)
    {
        for (int i = 0; i < mons.Count; i++)
            if (DisplayUtil.IsVirtualDisplay(mons[i].name, mons[i].hmon)) return i;
        return -1;
    }

    // ===== BƯỚC 4: đặt Text scale RIÊNG màn ảo = 125% (best-effort) =====
    // Lưu ý: "Text size" (TextScaleUtil) là global; Per-monitor DPI lại nằm HKCU\...\PerMonitorSettings.
    // Ở đây cố gắng đặt per-monitor bằng cách dò subkey mới nhất có thay đổi sau khi ép 4802x1802 (heuristic).
    public static void SetPerMonitorScaleForVirtual_125()
    {
        try
        {
            // Heuristic: tìm subkey mới nhất dưới PerMonitorSettings và đặt DpiValue=120 (125%)
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\PerMonitorSettings", true)
                          ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop\PerMonitorSettings", true);

            string? pick = root.GetSubKeyNames()
                .Select(n => (n, k: root.OpenSubKey(n, true)))
                .Where(t => t.k != null)
                .OrderByDescending(t => (t.k!.LastWriteTimeUtc))
                .Select(t => t.n)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(pick))
            {
                using var k = root.OpenSubKey(pick, true)!;
                int logPx = 120; // 96=100%; 120=125%
                k.SetValue("DpiValue", logPx, RegistryValueKind.DWord);
                Console.WriteLine($"[DPI] Set PerMonitorSettings\\{pick}\\DpiValue=120 (125%).");
                DpiUtil.BroadcastForSettingsChange();
            }
            else
            {
                Console.WriteLine("[DPI] No PerMonitorSettings subkey found; falling back to global TextScale=125%.");
                TextScaleUtil.SetPercent(125); // global fallback
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DPI] Failed per-monitor set: " + ex.Message);
            Console.WriteLine("[DPI] Fallback: global TextScale=125%.");
            try { TextScaleUtil.SetPercent(125); } catch { }
        }
    }

    // ===== BƯỚC 5: chia layout 6 mảnh 1600x900, cách 1 px, gắn nhãn 0..5 =====
    public struct RectI { public int x, y, w, h; public RectI(int X, int Y, int W, int H) { x = X; y = Y; w = W; h = H; } }
    public static IReadOnlyList<RectI> GetSixTiles_1600x900_with_1px_gutter()
    {
        const int cw = 1600, ch = 900, sep = 1;
        int x0 = 0, x1 = cw + sep, x2 = cw * 2 + sep * 2; // 0, 1601, 3202
        int y0 = 0, y1 = ch + sep;               // 0, 901

        return new[]{
            new RectI(x0,y0,cw,ch), // ID 0 (Main Left)
            new RectI(x1,y0,cw,ch), // ID 1 (Main Center)
            new RectI(x2,y0,cw,ch), // ID 2 (Main Right)
            new RectI(x0,y1,cw,ch), // ID 3 (Sub Left)
            new RectI(x1,y1,cw,ch), // ID 4 (Sub Center)
            new RectI(x2,y1,cw,ch), // ID 5 (Sub Right)
        };
    }

    // ===== BƯỚC 6: chỉ tạo đơn luồng remote của màn ảo, bind 0/1/2 -> left/center/right =====
    // Ở tầng WebSocket/Signal server, ta sẽ giới hạn 1 capture cho monitor mid=virtual,
    // và cung cấp /api/cluster cố định map 0,1,2 là main (left,center,right).
    public static void EnforceSingleRemoteForVirtual(ref SignalAndRestServer server)
    {
        // Hiện server đã gửi/nhận 1 kết nối -> 1 luồng capture (theo mid hoặc wid).
        // Nếu muốn chặn tạo thêm trên cùng monitor, bạn có thể:
        // - Trước khi nhận kết nối mới mid=V, đóng luồng cũ (nếu có).
        // Phần này đã có chỗ để bạn chèn trong HandleClient (kiểm soát bằng _streams/_captures).
        Console.WriteLine("[Remote] Single-stream policy: ensure only the virtual monitor stream stays active.");
    }
}
