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

partial class Program
{
    static async Task Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== RemotePlayServer (.NET 9 + Windows Graphics Capture + WebRTC) ===");

        // Liệt kê các cửa sổ capturable (snapshot ban đầu)
        var windows = Win32.ListTopLevelWindows()
            .Where(w => !string.IsNullOrWhiteSpace(w.title))
            .Where(w => WgcInterop.IsCapturableWindow(w.hwnd))
            .ToList();

        for (int i = 0; i < windows.Count; i++) Console.WriteLine($"{i,3}: {windows[i].title}");
        if (windows.Count == 0) Console.WriteLine("(!) Không tìm thấy cửa sổ.");

        // Liệt kê monitor (bao gồm màn hình ảo nếu đã mount)
        var monitors = WgcInterop.ListMonitorsDXGI();
        Console.WriteLine("=== Monitors ===");
        for (int i = 0; i < monitors.Count; i++)
            Console.WriteLine($"{i,3}: {monitors[i].name}  {monitors[i].width}x{monitors[i].height}");

        // ⭐ Tự chọn màn hình ảo nếu có
        int midVirtual = MonitorDetect.PickVirtualMid(monitors);
        if (midVirtual >= 0)
        {
            Console.WriteLine($"[AutoPick] Virtual display ≈ mid={midVirtual} ({monitors[midVirtual].name})");
            if (DisplayUtil.ForceResolution(monitors[midVirtual].name, 1366, 768, 60))
                Console.WriteLine("[Display] Forced 1366x768@60");
            else
                Console.WriteLine("[Display] ForceResolution failed (may already be 1366x768 or driver blocks change)");
        }
        else
        {
            Console.WriteLine("[AutoPick] Could not find a virtual display. Using last display as fallback.");
            midVirtual = monitors.Count - 1;
        }
        ForceVirtualDisplayTo1366x76860(midVirtual);

        int port = 8288;
        var server = new SignalAndRestServer($"http://+:{port}/");
        server.SetWindows(windows);
        server.SetMonitors(monitors.Select(m => (m.hmon, m.name, m.width, m.height)).ToList());
        await server.StartAsync();

        foreach (var ip in NetUtil.GetLocalIPv4Addresses())
            Console.WriteLine($"   • ws://{ip}:{port}/signal?wid=<id>   hoặc   ws://{ip}:{port}/signal?mid=<id>");
        Console.WriteLine($"   • http://localhost:{port}/api/windows");
        Console.WriteLine($"   • http://localhost:{port}/api/monitors");
        Console.WriteLine($"   • http://localhost:{port}/api/cluster");
        Console.WriteLine("Server is running. Press ENTER to exit.");
        Console.ReadLine();
        await server.StopAsync();
    }

    static void ForceVirtualDisplayTo1366x76860(int mid)
    {
        var mons = WgcInterop.ListMonitorsDXGI(); // (hmon, name, w, h)
        if (mid >= 0 && mid < mons.Count)
        {
            string devName = mons[mid].name; // ví dụ "\\.\DISPLAY5"
            Console.WriteLine("[Display] Forcing " + devName + " -> 1366x768@60");
            bool ok = DisplayUtil.ForceResolution(devName, 1366, 768, 60);
            Console.WriteLine(ok ? "[Display] OK" : "[Display] Failed to set mode");
        }
    }
}

// ====================== Cluster planner (Trái/Giữa/Phải) ======================
public record ClusterMap(int leftMid, int centerMid, int rightMid);

static class ClusterPlanner
{
    public static ClusterMap ComputeThreeScreenCluster(
    List<(IntPtr hmon, string name, int w, int h)> mons)
    {
        if (mons == null || mons.Count == 0) return new ClusterMap(-1, -1, -1);

        // CENTER = Primary (không có thì DISPLAY1, rồi 0)
        int midCenter = -1;
        for (int i = 0; i < mons.Count; i++)
            if (DisplayUtil.IsPrimary(mons[i].name)) { midCenter = i; break; }
        if (midCenter < 0)
            for (int i = 0; i < mons.Count; i++)
                if (mons[i].name.Equals(@"\\.\DISPLAY1", StringComparison.OrdinalIgnoreCase)) { midCenter = i; break; }
        if (midCenter < 0) midCenter = 0;

        // LEFT = Virtual khác center
        int midVirtual = -1;
        for (int i = 0; i < mons.Count; i++)
        {
            if (i == midCenter) continue;
            if (DisplayUtil.IsVirtualDisplay(mons[i].name, mons[i].hmon)) { midVirtual = i; break; }
        }

        // RIGHT = Physical khác center & khác virtual
        int midPhysical = -1;
        for (int i = 0; i < mons.Count; i++)
        {
            if (i == midCenter || i == midVirtual) continue;
            if (!DisplayUtil.IsVirtualDisplay(mons[i].name, mons[i].hmon)) { midPhysical = i; break; }
        }

        // Fallback nếu thiếu 1 bên: lấp bằng bất kỳ còn trống
        if (midVirtual < 0)
            for (int i = 0; i < mons.Count; i++)
                if (i != midCenter && i != midPhysical) { midVirtual = i; break; }

        if (midPhysical < 0)
            for (int i = 0; i < mons.Count; i++)
                if (i != midCenter && i != midVirtual) { midPhysical = i; break; }

        return new ClusterMap(midVirtual, midCenter, midPhysical);
    }
}

static class ClusterId
{
    public static int DisplayOrdinalFromName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return -1;
        var key = "DISPLAY";
        var idx = deviceName.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return -1;
        idx += key.Length;
        int j = idx;
        while (j < deviceName.Length && char.IsDigit(deviceName[j])) j++;
        var digits = deviceName.Substring(idx, j - idx);
        return int.TryParse(digits, out var n) ? n : -1;
    }

    // Chuyển mid (index) -> ordinal (1..N), fallback = mid+1 nếu parse lỗi
    public static int MidToOrdinal(List<(IntPtr hmon, string name, int w, int h)> mons, int mid)
    {
        if (mid < 0 || mid >= mons.Count) return -1;
        int ord = DisplayOrdinalFromName(mons[mid].name);
        return ord > 0 ? ord : (mid + 1);
    }
}

// ====================== HTTP + WebRTC server ======================
public class SignalAndRestServer
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<Guid, WebRTCStreamer_H264> _streams = new();
    private readonly ConcurrentDictionary<Guid, (int wid, WgcCapture cap)> _captures = new();

    private List<Win32.WindowInfo> _windows = new();
    private List<(IntPtr hmon, string name, int w, int h)> _monitors = new();

    // (Tuỳ chọn) Ép 1366×768@60 cho hai màn phụ để đồng bộ tỉ lệ
    static void ForceClusterSidesTo1366x768(List<(IntPtr hmon, string name, int w, int h)> mons, ClusterMap cluster)
    {
        void Force(int mid)
        {
            if (mid >= 0 && mid < mons.Count)
                try { DisplayUtil.ForceResolution(mons[mid].name, 1366, 768, 60); } catch { }
        }
        Force(cluster.leftMid);
        Force(cluster.rightMid);
    }

    public SignalAndRestServer(string prefix)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
    }

    public void SetWindows(List<Win32.WindowInfo> wins) => _windows = wins;
    public void SetMonitors(List<(IntPtr hmon, string name, int w, int h)> mons) => _monitors = mons;

    public Task StartAsync()
    {
        _listener.Start();
        Console.WriteLine($"[HTTP] Listening at: {string.Join(", ", _listener.Prefixes)}");
        _ = Task.Run(AcceptLoop);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _listener.Stop(); } catch { }
        foreach (var kv in _streams) { try { await kv.Value.StopAsync(); } catch { } try { kv.Value.Dispose(); } catch { } }
        foreach (var kv in _captures) { try { kv.Value.cap.Dispose(); } catch { } }
        _streams.Clear(); _captures.Clear();
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            var path = ctx.Request.Url!.AbsolutePath;

            if (path == "/api/windows" && ctx.Request.HttpMethod == "GET")
            {
                // ⭐ Luôn quét lại để thấy app vừa fullscreen/thoát fullscreen
                var winsNow = Win32.ListTopLevelWindows()
                    .Where(w => !string.IsNullOrWhiteSpace(w.title))
                    .Where(w => WgcInterop.IsCapturableWindow(w.hwnd))
                    .ToList();
                _windows = winsNow;

                var arr = System.Text.Json.JsonSerializer.Serialize(
                    winsNow.Select((w, i) => new { id = i, title = w.title }));
                var bytes = Encoding.UTF8.GetBytes(arr);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
                continue;
            }

            if (path == "/api/monitors" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();

                var arr = System.Text.Json.JsonSerializer.Serialize(
                    _monitors.Select((m, i) => new { id = i, name = m.name, w = m.w, h = m.h }));
                var bytes = Encoding.UTF8.GetBytes(arr);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
                continue;
            }

            // >>> ADD: API trả sơ đồ bind cụm 3 màn
            if (path == "/api/cluster" && ctx.Request.HttpMethod == "GET")
            {
                try
                {
                    var monsNow = WgcInterop.ListMonitorsDXGI();
                    _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();

                    var cluster = ClusterPlanner.ComputeThreeScreenCluster(_monitors);

                    // Trả trực tiếp MID (0-based) thay vì ordinal 1..N
                    int L = cluster.leftMid;
                    int C = cluster.centerMid;
                    int R = cluster.rightMid;

                    // Nếu thiếu (=-1) mà vẫn có >=3 màn, lấp bằng id chưa dùng
                    if (_monitors.Count >= 3)
                    {
                        var used = new HashSet<int> { L, C, R };
                        for (int i = 0; i < _monitors.Count; i++)
                        {
                            if (used.Contains(i)) continue;
                            if (L < 0) { L = i; used.Add(i); continue; }
                            if (R < 0) { R = i; used.Add(i); continue; }
                        }
                    }

                    string json = $"{{\"left\":{Math.Max(0, L)},\"center\":{Math.Max(0, C)},\"right\":{Math.Max(0, R)}}}";
                    var bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
                catch (Exception ex)
                {
                    var bytes = Encoding.UTF8.GetBytes("{\"left\":1,\"center\":1,\"right\":1}");
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                    Console.WriteLine("[/api/cluster] " + ex);
                }
                continue;
            }

            if (ctx.Request.IsWebSocketRequest && path == "/signal")
            {
                var qs = HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
                var widStr = qs.Get("wid");
                var midStr = qs.Get("mid");

                int wid = -1, mid = -1;
                if (!string.IsNullOrWhiteSpace(widStr)) int.TryParse(widStr, out wid);
                if (!string.IsNullOrWhiteSpace(midStr)) int.TryParse(midStr, out mid);

                if (wid < 0 && mid < 0) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                if (wid >= _windows.Count && mid >= _monitors.Count) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }

                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var id = Guid.NewGuid();
                Console.WriteLine($"[Signal] Client connected {id}, wid={wid}, mid={mid}");

                _ = Task.Run(() => HandleClient(id, wid, mid, wsCtx.WebSocket, qs));
                continue;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    }

    [DllImport("combase.dll")] static extern int RoInitialize(uint initType); // 1 = RO_INIT_MULTITHREADED

    private async Task HandleClient(Guid id, int wid, int mid, System.Net.WebSockets.WebSocket ws, System.Collections.Specialized.NameValueCollection qs)
    {
        CancellationTokenSource? stopCapture = null;
        Thread? capThread = null;

        try
        {
            // 1) Nhận "offer:..."
            string? offer = null;
            var recvBuf = new byte[256 * 1024];
            using var ms = new System.IO.MemoryStream();

            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), CancellationToken.None);
                if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                ms.Write(recvBuf, 0, res.Count);
                if (!res.EndOfMessage) continue;

                var text = Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                if (text.StartsWith("offer:", StringComparison.OrdinalIgnoreCase)) { offer = text.Substring("offer:".Length); break; }
                else if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    var pong = Encoding.UTF8.GetBytes("pong");
                    await ws.SendAsync(new ArraySegment<byte>(pong), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            if (offer == null) return;

            // Tham số codec từ query (tuỳ chọn): fps/kbps/crf/preset/zerolat
            int fps = TryParseInt(qs.Get("fps"), 60, 5, 120);
            int kbps = TryParseInt(qs.Get("kbps"), 12000, 0, 100000);
            int crf = TryParseInt(qs.Get("crf"), 20, 0, 40);
            string preset = qs.Get("preset") ?? "veryfast";
            bool zerolat = TryParseInt(qs.Get("zerolat"), 1, 0, 1) == 1;

            // 2) WebRTC: tạo streamer, trả answer
            var streamer = new WebRTCStreamer_H264(fps: fps, targetKbps: kbps, crf: crf, preset: preset, zerolatency: zerolat);
            await streamer.StartAsync();
            var answer = await streamer.SetRemoteOfferAndCreateAnswerAsync(offer);
            {
                var data = Encoding.UTF8.GetBytes("answer:" + answer);
                await ws.SendAsync(new ArraySegment<byte>(data), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
            _streams[id] = streamer;

            // 3) Khởi động capture trên thread riêng
            stopCapture = new CancellationTokenSource();

            capThread = new Thread(() =>
            {
                try { RoInitialize(1); } catch { }
                try { WinRT.ComWrappersSupport.InitializeComWrappers(); } catch { }

                try
                {
                    WgcCapture cap;
                    if (mid >= 0 && mid < _monitors.Count)
                    {
                        var hmon = _monitors[mid].hmon;
                        cap = new WgcCapture(hmon, isMonitor: true);
                    }
                    else
                    {
                        var hwnd = _windows[wid].hwnd;
                        cap = new WgcCapture(hwnd);
                    }
                    _captures[id] = (wid, cap);

                    Action<byte[], int, int, int> handler = (buf, w, h, stride) =>
                    {
                        try { if (streamer.IsRunning) streamer.PushBgraBytesAsync(buf, w, h, stride); }
                        catch (Exception ex) { Console.WriteLine("[WGC->RTC] push error: " + ex); }
                    };
                    cap.OnFrame += handler;

                    // Nếu peer rớt, ngắt capture ngay
                    streamer.OnPeerDisconnected += () =>
                    {
                        try { stopCapture?.Cancel(); } catch { }
                    };

                    cap.Start();
                    stopCapture.Token.WaitHandle.WaitOne();

                    // gỡ handler trước khi dispose để tránh race
                    try { cap.OnFrame -= handler; } catch { }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Capture] start error: " + ex);
                }
            })
            { IsBackground = true, Name = $"WGC-Capture-{(mid >= 0 ? $"mid{mid}" : $"wid{wid}")}" };
            capThread.Start();

            if (mid >= 0) Console.WriteLine($"[WGC] Streaming monitor mid={mid}");
            else Console.WriteLine($"[WGC] Streaming window wid={wid}");

            while (ws.State == System.Net.WebSockets.WebSocketState.Open) await Task.Delay(250);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signal] client error: {ex.Message}");
        }
        finally
        {
            try
            {
                if (stopCapture != null) stopCapture.Cancel();
                if (capThread != null && capThread.IsAlive) { try { capThread.Join(500); } catch { } }
            }
            catch { }

            if (_streams.TryRemove(id, out var st)) { try { st.StopAsync().Wait(500); } catch { } try { st.Dispose(); } catch { } }
            if (_captures.TryRemove(id, out var cap)) { try { cap.cap.Dispose(); } catch { } }

            try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            Console.WriteLine($"[Signal] Client disconnected {id}");
        }
    }

    static int TryParseInt(string? s, int def, int min, int max)
    {
        if (!int.TryParse(s, out var v)) return def;
        return Math.Clamp(v, min, max);
    }
}

// ====================== WebRTC interface (giữ nguyên) ======================
public interface IWebRTCStreamer : IDisposable
{
    Task StartAsync();
    Task StopAsync();
    Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp);
    Task PushBgraBytesAsync(byte[] src, int width, int height, int stride);
}

// ====================== Virtual monitor heuristics (giữ nguyên, bổ sung dùng chung) ======================
static class MonitorDetect
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    // Physical Monitor API (fallback)
    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    static bool IsLikelyVirtualByStrings(string deviceString, string deviceId)
    {
        var s = (deviceString ?? "").ToLowerInvariant();
        var id = (deviceId ?? "").ToLowerInvariant();
        string[] keywords = { "virtual", "idd", "indirect", "headless" };
        return keywords.Any(k => s.Contains(k) || id.Contains(k));
    }

    static bool HasNoPhysicalMonitors(IntPtr hmon)
    {
        try { return GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out var n) && n == 0; }
        catch { return false; }
    }

    /// Trả về true nếu \\.\DISPLAYx trông giống màn hình ảo.
    public static bool IsVirtualDisplay(string displayName, IntPtr hmon)
    {
        for (uint devNum = 0; ; devNum++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, devNum, ref dd, 0)) break;
            if (!string.Equals(dd.DeviceName, displayName, StringComparison.OrdinalIgnoreCase))
                continue;

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0))
            {
                if (IsLikelyVirtualByStrings(mon.DeviceString, mon.DeviceID))
                    return true;
            }
            return HasNoPhysicalMonitors(hmon);
        }
        return HasNoPhysicalMonitors(hmon);
    }

    /// Chọn mid của màn hình ảo trong danh sách monitors DXGI (ưu tiên có từ khóa).
    public static int PickVirtualMid(List<(IntPtr hmon, string name, int width, int height)> mons)
    {
        for (int i = 0; i < mons.Count; i++)
            if (IsVirtualDisplay(mons[i].name, mons[i].hmon))
                return i;

        int[] favW = { 1366, 1280, 1024, 800 };
        int[] favH = { 768, 720, 768, 600 };
        for (int i = 0; i < mons.Count; i++)
            for (int k = 0; k < favW.Length; k++)
                if (mons[i].width == favW[k] && mons[i].height == favH[k])
                    return i;

        return mons.Count > 0 ? mons.Count - 1 : -1;
    }
}
