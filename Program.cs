
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
    [DllImport("combase.dll")]
    static extern int RoInitialize(uint initType);
    static async Task Main()
    {
        RoInitialize(1);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== RemotePlayServer (.NET 9 + Windows Graphics Capture + WebRTC) ===");

        var windows = Win32.ListTopLevelWindows().Where(w => !string.IsNullOrWhiteSpace(w.title)).Where(w => WgcInterop.IsCapturableWindow(w.hwnd)).ToList();
        if (windows.Count == 0) { Console.WriteLine("Không tìm thấy cửa sổ."); return; }
        for (int i = 0; i < windows.Count; i++) Console.WriteLine($"{i,3}: {windows[i].title}");

        int port = 8288;
        var server = new SignalAndRestServer($"http://+:{port}/");
        server.SetWindows(windows);
        await server.StartAsync();

        foreach (var ip in NetUtil.GetLocalIPv4Addresses())
            Console.WriteLine($"   • ws://{ip}:{port}/signal?wid=<id>");
        Console.WriteLine($"   • http://localhost:{port}/api/windows");

        // Keep running
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
        foreach (var kv in _streams)
        {
            try { await kv.Value.StopAsync(); } catch { }
            try { kv.Value.Dispose(); } catch { }
        }
        foreach (var kv in _captures)
        {
            try { kv.Value.cap.Dispose(); } catch { }
        }
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
                if (!int.TryParse(widStr, out var wid) || wid < 0 || wid >= _windows.Count)
                {
                    ctx.Response.StatusCode = 400; ctx.Response.Close(); continue;
                }

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

    private async Task HandleClient(Guid id, int wid, System.Net.WebSockets.WebSocket ws)
    {
        try
        {
            // Wait for "offer:..."
            string? offer = null;
            var recvBuf = new byte[256 * 1024];
            var ms = new System.IO.MemoryStream();
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), CancellationToken.None);
                if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                ms.Write(recvBuf, 0, res.Count);
                if (!res.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                if (text.StartsWith("offer:"))
                {
                    offer = text.Substring("offer:".Length);
                    break;
                }
                else if (text == "ping")
                {
                    var pong = Encoding.UTF8.GetBytes("pong");
                    await ws.SendAsync(new ArraySegment<byte>(pong), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            if (offer == null) return;

            // Set up WebRTC
            var streamer = new WebRTCStreamer(fps: 60);
            await streamer.StartAsync();
            var answer = await streamer.SetRemoteOfferAndCreateAnswerAsync(offer);
            {
                var data = Encoding.UTF8.GetBytes("answer:" + answer);
                await ws.SendAsync(new ArraySegment<byte>(data), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
            _streams[id] = streamer;

            // Start capture for this window
            var hwnd = _windows[wid].hwnd;
            WgcCapture cap;
            try
            {
                cap = new WgcCapture(hwnd);
                _captures[id] = (wid, cap);

                // Match output size to window size first; can be changed if needed
                var (cw, ch) = cap.Size;
                await streamer.StartVideoAsync((int)cw, (int)ch);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WGC] Failed to capture window {wid} ({_windows[wid].title}): {ex.Message}");
                throw; // Re-throw to trigger connection cleanup
            }

            cap.OnFrame += (buf, stride, w, h) =>
            {
                // Push latest; WebRTCStreamer has bounded drop-oldest channel
                streamer.PushBgraBytesAsync(buf, w, h, stride);
            };
            cap.Start();
            Console.WriteLine($"[WGC] Streaming wid={wid} size={cap.Size.w}x{cap.Size.h}");

            // keep socket open until close
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signal] client error: {ex.Message}");
        }
        finally
        {
            if (_streams.TryRemove(id, out var st))
            {
                try { st.StopAsync().Wait(500); } catch { }
                try { st.Dispose(); } catch { }
            }
            if (_captures.TryRemove(id, out var cap))
            {
                try { cap.cap.Dispose(); } catch { }
            }
            try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            Console.WriteLine($"[Signal] Client disconnected {id}");
        }
    }
}
