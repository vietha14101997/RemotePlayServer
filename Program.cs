#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using System.Runtime.InteropServices;

class Program
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

        int port = 8288;
        var server = new SignalAndRestServer($"http://+:{port}/");
        server.SetWindows(windows);
        server.SetMonitors(monitors);
        await server.StartAsync();

        foreach (var ip in NetUtil.GetLocalIPv4Addresses())
            Console.WriteLine($"   • ws://{ip}:{port}/signal?wid=<id>   hoặc   ws://{ip}:{port}/signal?mid=<id>");
        Console.WriteLine($"   • http://localhost:{port}/api/windows");
        Console.WriteLine($"   • http://localhost:{port}/api/monitors");
        Console.WriteLine("Server is running. Press ENTER to exit.");
        Console.ReadLine();
        await server.StopAsync();
    }
}

public class SignalAndRestServer
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<Guid, WebRTCStreamer_H264> _streams = new();
    private readonly ConcurrentDictionary<Guid, (int wid, WgcCapture cap)> _captures = new();

    private System.Collections.Generic.List<Win32.WindowInfo> _windows = new();
    private System.Collections.Generic.List<(IntPtr hmon, string name, int w, int h)> _monitors = new();

    public SignalAndRestServer(string prefix)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
    }

    public void SetWindows(System.Collections.Generic.List<Win32.WindowInfo> wins) => _windows = wins;
    public void SetMonitors(System.Collections.Generic.List<(IntPtr hmon, string name, int w, int h)> mons) => _monitors = mons;

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

public interface IWebRTCStreamer : IDisposable
{
    Task StartAsync();
    Task StopAsync();
    Task<string> SetRemoteOfferAndCreateAnswerAsync(string offerSdp);
    Task PushBgraBytesAsync(byte[] src, int width, int height, int stride);
}
