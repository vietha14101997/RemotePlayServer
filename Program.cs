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

        // Chỉ liệt kê cửa sổ có thể capture được (tránh cloaked/tool/invisible)
        var windows = Win32.ListTopLevelWindows()
            .Where(w => !string.IsNullOrWhiteSpace(w.title))
            .Where(w => WgcInterop.IsCapturableWindow(w.hwnd))
            .ToList();

        if (windows.Count == 0) { Console.WriteLine("Không tìm thấy cửa sổ."); return; }
        for (int i = 0; i < windows.Count; i++) Console.WriteLine($"{i,3}: {windows[i].title}");

        int port = 8288;
        var server = new SignalAndRestServer($"http://+:{port}/");
        server.SetWindows(windows);
        await server.StartAsync();

        foreach (var ip in NetUtil.GetLocalIPv4Addresses())
            Console.WriteLine($"   • ws://{ip}:{port}/signal?wid=<id>");
        Console.WriteLine($"   • http://localhost:{port}/api/windows");
        Console.WriteLine("Server is running. Press ENTER to exit.");
        Console.ReadLine();
        await server.StopAsync();
    }
}

public class SignalAndRestServer
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<Guid, WebRTCStreamer> _streams = new();
    private readonly ConcurrentDictionary<Guid, (int wid, WgcCapture cap)> _captures = new();
    private System.Collections.Generic.List<Win32.WindowInfo> _windows = new();

    public SignalAndRestServer(string prefix)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
    }

    public void SetWindows(System.Collections.Generic.List<Win32.WindowInfo> wins) => _windows = wins;

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
                var arr = System.Text.Json.JsonSerializer.Serialize(_windows.Select((w, i) => new { id = i, title = w.title }));
                var bytes = Encoding.UTF8.GetBytes(arr);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
                continue;
            }

            if (ctx.Request.IsWebSocketRequest && path == "/signal")
            {
                var widStr = HttpUtility.ParseQueryString(ctx.Request.Url!.Query).Get("wid");
                if (!int.TryParse(widStr, out var wid) || wid < 0 || wid >= _windows.Count) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }

                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var id = Guid.NewGuid();
                Console.WriteLine($"[Signal] Client connected {id}, wid={wid}");

                _ = Task.Run(() => HandleClient(id, wid, wsCtx.WebSocket));
                continue;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    }

    [DllImport("combase.dll")] static extern int RoInitialize(uint initType); // 1 = RO_INIT_MULTITHREADED

    private async Task HandleClient(Guid id, int wid, System.Net.WebSockets.WebSocket ws)
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

            // 2) WebRTC: tạo streamer, trả answer
            var streamer = new WebRTCStreamer(fps: 60);
            await streamer.StartAsync();
            var answer = await streamer.SetRemoteOfferAndCreateAnswerAsync(offer);
            {
                var data = Encoding.UTF8.GetBytes("answer:" + answer);
                await ws.SendAsync(new ArraySegment<byte>(data), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
            _streams[id] = streamer; // quản lý vòng đời :contentReference[oaicite: 0]{ index = 0}

            // 3) Khởi động capture trên thread riêng (RoInitialize + ComWrappersSupport)
            var hwnd = _windows[wid].hwnd;
            stopCapture = new CancellationTokenSource();

            capThread = new Thread(() =>
            {
                try { RoInitialize(1); } catch { }
                try { WinRT.ComWrappersSupport.InitializeComWrappers(); } catch { }

                try
                {
                    var cap = new WgcCapture(hwnd);               // tạo pool + session  :contentReference[oaicite:1]{index=1}
                    _captures[id] = (wid, cap);

                    // Đẩy frame ra WebRTC
                    cap.OnFrame += (buf, w, h, stride) =>
                    {
                        try { streamer.PushBgraBytesAsync(buf, w, h, stride); }
                        catch (Exception ex) { Console.WriteLine("[WGC->RTC] push error: " + ex); }
                    };

                    cap.Start();                                   // bắt đầu nhận frame  :contentReference[oaicite:2]{index=2}

                    while (!stopCapture!.IsCancellationRequested) Thread.Sleep(15);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Capture] start error: " + ex);
                }
            })
            { IsBackground = true, Name = $"WGC-Capture-{wid}" };
            capThread.Start();

            Console.WriteLine($"[WGC] Streaming wid={wid}");
            while (ws.State == System.Net.WebSockets.WebSocketState.Open) await Task.Delay(500);
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
}
