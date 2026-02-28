#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using RemotePlayServer.Infrastructure.Capture;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Infrastructure.Hardware;
using RemotePlayServer.Infrastructure;
using RemotePlayServer.Application.Protocol;

namespace RemotePlayServer.Server;

/// <summary>
/// HTTP/WebSocket signal server handling REST APIs and WebRTC signaling.
/// </summary>
public class SignalServer
{
    private readonly HttpListener _listener;

    private List<Win32.WindowInfo> _windows = new();
    private List<(IntPtr hmon, string name, int w, int h)> _monitors = new();

    public SignalServer(string prefix) { _listener = new HttpListener(); _listener.Prefixes.Add(prefix); }
    public void SetWindows(List<Win32.WindowInfo> wins) => _windows = wins;
    public void SetMonitors(List<(IntPtr hmon, string name, int w, int h)> mons) => _monitors = mons;

    public Task StartAsync() { _listener.Start(); _ = Task.Run(AcceptLoop); Console.WriteLine($"[HTTP] {string.Join(", ", _listener.Prefixes)}"); return Task.CompletedTask; }
    public async Task StopAsync()
    {
        try { _listener.Stop(); } catch { }
        await Task.CompletedTask;
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx; try { ctx = await _listener.GetContextAsync(); } catch { break; }
            var path = ctx.Request.Url!.AbsolutePath;

            // Quick validation endpoint
            if (path == "/ping" && ctx.Request.HttpMethod == "GET")
            {
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                var body = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"version\":\"2.0\"}");
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.Close();
                continue;
            }

            if (path == "/api/monitors" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                var arr = System.Text.Json.JsonSerializer.Serialize(
                    _monitors.Select((m, i) => new { id = i, name = m.name, w = m.w, h = m.h }));
                var b = Encoding.UTF8.GetBytes(arr);
                ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.Close(); continue;
            }

            if (path == "/api/hwinfo" && ctx.Request.HttpMethod == "GET")
            {
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                try
                {
                    var hwInfo = await HardwareInfoGatherer.GetHardwareInfoAsync();
                    var encoderInfo = HardwareInfoGatherer.GetEncoderInfo();
                    var monsNow = WgcInterop.ListMonitorsDXGI();
                    _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();

                    var response = new
                    {
                        device = new
                        {
                            name = hwInfo.DeviceName,
                            processor = hwInfo.Processor.Name,
                            gpu = hwInfo.Gpu.Name,
                            gpuVramMB = hwInfo.Gpu.VramMB,
                            ramMB = hwInfo.Ram.TotalMB,
                            os = $"{hwInfo.Os.Name} {hwInfo.Os.Version}"
                        },
                        encoder = new
                        {
                            type = encoderInfo.Type,
                            hwAccel = encoderInfo.HwAccel
                        },
                        monitors = _monitors.Select((m, i) => new
                        {
                            id = i,
                            name = m.name,
                            w = m.w,
                            h = m.h,
                            isVirtual = DisplayUtil.IsVirtualDisplay(m.name, m.hmon)
                        }),
                        network = new
                        {
                            connectionType = hwInfo.Network.ConnectionType,
                            speedMbps = hwInfo.Network.SpeedMbps,
                            ipAddress = hwInfo.Network.IpAddress
                        },
                        timestamp = hwInfo.Timestamp
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(response);
                    var b = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    Console.WriteLine("[API] /api/hwinfo -> returned hardware info");
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 500;
                    var errorJson = $"{{\"error\":\"{ex.Message}\"}}";
                    var errorBytes = Encoding.UTF8.GetBytes(errorJson);
                    ctx.Response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
                }
                ctx.Response.Close();
                continue;
            }

            if (path == "/api/layout" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();

                if (_monitors.Count == 0)
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                    continue;
                }

                int gap = 1;
                int rawFrameWidth = 0;
                int maxHeight = 0;

                foreach (var mon in _monitors)
                {
                    rawFrameWidth += mon.w;
                    if (mon.h > maxHeight) maxHeight = mon.h;
                }
                rawFrameWidth += (_monitors.Count - 1) * gap;

                const int NVENC_MAX_WIDTH = 4096;
                int frameWidth, frameHeight, cellWidth, cellHeight, finalGap;

                if (rawFrameWidth > NVENC_MAX_WIDTH)
                {
                    int excess = rawFrameWidth - NVENC_MAX_WIDTH;
                    int trimPerMonitor = (excess + _monitors.Count - 1) / _monitors.Count;

                    cellWidth = _monitors[0].w - trimPerMonitor;
                    cellHeight = maxHeight;
                    frameWidth = cellWidth * _monitors.Count + (_monitors.Count - 1) * gap;
                    frameHeight = maxHeight;
                    finalGap = gap;

                    if (frameWidth > NVENC_MAX_WIDTH)
                    {
                        frameWidth = NVENC_MAX_WIDTH;
                        cellWidth = (NVENC_MAX_WIDTH - (_monitors.Count - 1) * gap) / _monitors.Count;
                    }
                }
                else
                {
                    frameWidth = rawFrameWidth;
                    frameHeight = maxHeight;
                    cellWidth = _monitors[0].w;
                    cellHeight = _monitors[0].h;
                    finalGap = gap;
                }

                string json = $"{{\"frameWidth\":{frameWidth},\"frameHeight\":{frameHeight},\"cellWidth\":{cellWidth},\"cellHeight\":{cellHeight},\"gap\":{finalGap},\"monitors\":{_monitors.Count}}}";
                var b2 = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(b2, 0, b2.Length);
                ctx.Response.Close();
                Console.WriteLine($"[HTTP] /api/layout -> {json}");
                continue;
            }

            if (path == "/api/cluster" && ctx.Request.HttpMethod == "GET")
            {
                var monsNow = WgcInterop.ListMonitorsDXGI();
                _monitors = monsNow.Select(m => (m.hmon, m.name, m.width, m.height)).ToList();
                int midVirt = _monitors.Count > 0 ? _monitors.Count - 1 : -1;

                string json = $"{{\"left\":{Math.Max(0, midVirt)},\"center\":{Math.Max(0, midVirt)},\"right\":{Math.Max(0, midVirt)}}}";
                var b = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.Close(); continue;
            }

            if (ctx.Request.IsWebSocketRequest && path == "/signal")
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var clientId = Guid.NewGuid();
                var remoteIp = ctx.Request.RemoteEndPoint?.Address;

                var query = ctx.Request.Url?.Query ?? "";
                var queryParams = HttpUtility.ParseQueryString(query);
                bool isUsbTransport = queryParams["transport"]?.Equals("usb", StringComparison.OrdinalIgnoreCase) == true;

                Console.WriteLine($"[Signal] Client connected {clientId}, protocol=v2, transport={(isUsbTransport ? "USB" : "WiFi")}");

                _ = Task.Run(async () =>
                {
                    var handler = new PhaseProtocolHandler(
                        clientId, wsCtx.WebSocket, remoteIp, CancellationToken.None, isUsbTransport);
                    await handler.HandleAsync();
                });
                continue;
            }

            // Serve static files from Web folder
            if (ctx.Request.HttpMethod == "GET" && !ctx.Request.IsWebSocketRequest)
            {
                var fileName = path.TrimStart('/');
                if (string.IsNullOrEmpty(fileName)) fileName = "index.html";

                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                var allowedExtensions = new[] { ".html", ".htm", ".js", ".css", ".json", ".png", ".jpg", ".gif", ".svg", ".ico" };

                if (allowedExtensions.Contains(ext))
                {
                    var webFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web");
                    var filePath = Path.Combine(webFolder, fileName);

                    var fullPath = Path.GetFullPath(filePath);
                    var fullWebFolder = Path.GetFullPath(webFolder);

                    if (fullPath.StartsWith(fullWebFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                    {
                        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = GetContentType(ext);

                        var fileBytes = await File.ReadAllBytesAsync(fullPath);
                        ctx.Response.ContentLength64 = fileBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
                        ctx.Response.Close();
                        Console.WriteLine($"[HTTP] Served static file: {fileName}");
                        continue;
                    }
                }
            }

            ctx.Response.StatusCode = 404; ctx.Response.Close();
        }
    }

    private static string GetContentType(string ext) => ext switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream"
    };

    [DllImport("combase.dll")] static extern int RoInitializeNative(uint initType);

    /// <summary>
    /// Initialize Windows Runtime for the current thread (needed for WGC).
    /// </summary>
    public static int RoInitialize(uint initType) => RoInitializeNative(initType);

    internal static int TryParseInt(string? s, int def, int min, int max) => int.TryParse(s, out var v) ? Math.Clamp(v, min, max) : def;

    public static async Task ForceCleanupResources()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100);
        }

        try
        {
            var processes = Process.GetProcessesByName("RemotePlayServer");
            foreach (var p in processes.Where(p => p.Id != Process.GetCurrentProcess().Id))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        await Task.Delay(1000);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
