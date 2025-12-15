#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Captures individual monitors separately (not combined).
/// Each monitor has its own D3D11 Device and capture thread for TRUE PARALLEL capture.
/// This eliminates context serialization bottleneck when capturing multiple monitors.
/// </summary>
public sealed class PerMonitorCapture : IDisposable
{
    public readonly int MonitorCount;
    public readonly int TargetFps;
    public readonly List<MonitorInfo> Monitors = new();

    public class MonitorInfo
    {
        public int Index { get; set; }
        public IntPtr HMon { get; set; }
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        
        // Per-monitor D3D11 resources (isolated - no context contention!)
        public ID3D11Device? Device { get; set; }
        public ID3D11DeviceContext? Context { get; set; }
        public IDXGIOutputDuplication? Duplication { get; set; }
        public ID3D11Texture2D? LastFrame { get; set; }
        public GpuColorConverter? ColorConverter { get; set; }
        
        // For NVIDIA software conversion path (NV12 bytes, not texture)
        public ID3D11Texture2D? BgraStagingTexture { get; set; }
        public byte[]? Nv12Buffer { get; set; }
        
        // Per-monitor capture thread
        public Thread? CaptureThread { get; set; }
        public volatile bool Running;
        
        // GPU vendor detection per monitor
        public bool IsNvidia { get; set; }
        
        // FPS tracking for diagnostics
        public long CaptureFrameCount;
        public long LastFpsLogTime;
        public long LastFpsLogFrameCount;
    }
    
    private volatile bool _running;
    private string? _preferredGpu;

    /// <summary>
    /// Callback for each monitor's NV12 texture frame.
    /// Parameters: monitorIndex, nv12Texture, width, height, timestamp
    /// </summary>
    public event Action<int, ID3D11Texture2D, int, int, long>? OnMonitorFrame;
    
    /// <summary>
    /// Callback for each monitor's NV12 bytes frame (used for NVIDIA compatibility).
    /// Parameters: monitorIndex, nv12Bytes, width, height, timestamp
    /// </summary>
    public event Action<int, byte[], int, int, long>? OnMonitorNV12Bytes;

    /// <summary>
    /// Get D3D11 device for a specific monitor (for encoder initialization)
    /// </summary>
    public ID3D11Device? GetDeviceForMonitor(int monitorIndex)
    {
        if (monitorIndex >= 0 && monitorIndex < Monitors.Count)
            return Monitors[monitorIndex].Device;
        return null;
    }

    /// <summary>
    /// Expose first monitor's device for backward compatibility
    /// </summary>
    public ID3D11Device Device => Monitors.Count > 0 ? Monitors[0].Device! : throw new InvalidOperationException("No monitors");

    public PerMonitorCapture(List<(IntPtr hmon, string name, int w, int h)> monitors, int targetFps = 30, string? preferredGpu = null)
    {
        if (monitors == null || monitors.Count == 0)
            throw new ArgumentException("At least one monitor required");

        MonitorCount = monitors.Count;
        TargetFps = Math.Clamp(targetFps, 10, 60);
        _preferredGpu = preferredGpu;

        Console.WriteLine($"[PerMonitorCapture] Initializing {MonitorCount} monitors @ {TargetFps}fps (PARALLEL MODE - separate D3D11 devices)");

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // Setup each monitor with its OWN D3D11 device
        for (int i = 0; i < monitors.Count; i++)
        {
            var mon = monitors[i];
            var info = new MonitorInfo
            {
                Index = i,
                HMon = mon.hmon,
                Name = mon.name,
                Width = mon.w,
                Height = mon.h
            };

            // Find adapter for this monitor
            IDXGIAdapter1? monitorAdapter = null;
            IDXGIOutput? targetOutput = null;
            
            // First try preferred GPU
            if (!string.IsNullOrEmpty(preferredGpu))
            {
                for (uint ai = 0; ; ai++)
                {
                    if (factory.EnumAdapters1(ai, out var adapter).Failure) break;
                    var adapterDesc = adapter.Description.Description.ToLowerInvariant();
                    bool matches = preferredGpu.ToLowerInvariant() switch
                    {
                        "intel" => adapterDesc.Contains("intel"),
                        "amd" => adapterDesc.Contains("amd") || adapterDesc.Contains("radeon"),
                        "nvidia" => adapterDesc.Contains("nvidia") || adapterDesc.Contains("geforce"),
                        _ => false
                    };
                    if (matches)
                    {
                        // Check if this adapter has our monitor
                        for (uint oi = 0; ; oi++)
                        {
                            if (adapter.EnumOutputs(oi, out var output).Failure) break;
                            if (output.Description.Monitor == mon.hmon)
                            {
                                monitorAdapter = adapter;
                                targetOutput = output;
                                break;
                            }
                            output.Dispose();
                        }
                        if (monitorAdapter != null) break;
                    }
                    adapter.Dispose();
                }
            }
            
            // Fallback: find adapter by monitor handle
            if (monitorAdapter == null)
            {
                for (uint ai = 0; ; ai++)
                {
                    if (factory.EnumAdapters1(ai, out var adapter).Failure) break;
                    for (uint oi = 0; ; oi++)
                    {
                        if (adapter.EnumOutputs(oi, out var output).Failure) break;
                        if (output.Description.Monitor == mon.hmon)
                        {
                            monitorAdapter = adapter;
                            targetOutput = output;
                            break;
                        }
                        output.Dispose();
                    }
                    if (monitorAdapter != null) break;
                    adapter.Dispose();
                }
            }

            if (monitorAdapter == null)
            {
                Console.WriteLine($"[PerMonitorCapture] Warning: No adapter for monitor {i} ({mon.name})");
                Monitors.Add(info);
                continue;
            }

            // Detect GPU vendor for this monitor
            var adapterName = monitorAdapter.Description.Description.ToUpperInvariant();
            info.IsNvidia = adapterName.Contains("NVIDIA") || adapterName.Contains("GEFORCE") || 
                            adapterName.Contains("GTX") || adapterName.Contains("RTX");

            // Create SEPARATE D3D11 device for THIS monitor
            var levels = new FeatureLevel[] { FeatureLevel.Level_11_0 };
            ID3D11Device device;
            ID3D11DeviceContext context;
            D3D11.D3D11CreateDevice(
                monitorAdapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels,
                out device,
                out context
            );
            
            info.Device = device;
            info.Context = context;
            
            Console.WriteLine($"[PerMonitorCapture] Monitor {i}: Created D3D11 device (adapter={monitorAdapter.Description.Description}, NVIDIA={info.IsNvidia})");
            
            // Create output duplication using THIS monitor's device
            if (targetOutput != null)
            {
                using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
                targetOutput.Dispose();
                info.Duplication = output1.DuplicateOutput(device);
                Console.WriteLine($"[PerMonitorCapture] Monitor {i}: {mon.name} {mon.w}x{mon.h} - duplication ready");
            }
            
            monitorAdapter.Dispose();
            Monitors.Add(info);
        }

        Console.WriteLine($"[PerMonitorCapture] Initialized {Monitors.Count} monitors with SEPARATE D3D11 devices");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        // Start SEPARATE capture thread for EACH monitor (true parallelism!)
        foreach (var mon in Monitors)
        {
            if (mon.Device == null || mon.Duplication == null) continue;
            
            mon.Running = true;
            mon.CaptureThread = new Thread(() => CaptureLoopForMonitor(mon))
            {
                IsBackground = true,
                Name = $"Capture-Mon{mon.Index}"
            };
            mon.CaptureThread.Start();
        }
        
        Console.WriteLine($"[PerMonitorCapture] Started {Monitors.Count} parallel capture threads");
    }

    public void Stop()
    {
        _running = false;
        
        // Stop all monitor threads
        foreach (var mon in Monitors)
        {
            mon.Running = false;
        }
        
        // Wait for threads to finish
        foreach (var mon in Monitors)
        {
            mon.CaptureThread?.Join(1000);
        }
        
        Console.WriteLine("[PerMonitorCapture] All capture threads stopped");
    }

    /// <summary>
    /// Capture loop for a single monitor - runs in its own thread with its own D3D11 device
    /// No context contention with other monitors!
    /// </summary>
    private void CaptureLoopForMonitor(MonitorInfo mon)
    {
        int frameTimeMs = 1000 / TargetFps;
        Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: Capture thread started @ {TargetFps}fps");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        mon.LastFpsLogTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        mon.LastFpsLogFrameCount = 0;
        mon.CaptureFrameCount = 0;

        while (mon.Running && _running)
        {
            try
            {
                long captureTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                
                // FPS logging every 3 seconds
                long timeSinceLastLog = captureTimestamp - mon.LastFpsLogTime;
                if (timeSinceLastLog >= 3000)
                {
                    long framesSinceLastLog = mon.CaptureFrameCount - mon.LastFpsLogFrameCount;
                    double fps = framesSinceLastLog * 1000.0 / timeSinceLastLog;
                    Console.WriteLine($"[Capture FPS] Mon{mon.Index}: {fps:F1} fps (captured {framesSinceLastLog} frames in {timeSinceLastLog}ms)");
                    mon.LastFpsLogTime = captureTimestamp;
                    mon.LastFpsLogFrameCount = mon.CaptureFrameCount;
                }

                if (mon.Duplication == null) continue;

                // Use a longer timeout (2x frame time) to ensure we catch the VSync interval.
                // Short timeout (8ms) causes missed frames if thread timing drifts relative to VSync.
                int timeoutMs = frameTimeMs * 2;
                var result = mon.Duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);
                
                if (result.Success && desktopResource != null)
                {
                    mon.CaptureFrameCount++;
                    
                    try
                    {
                        using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
                        
                        if (mon.IsNvidia)
                        {
                            // NVIDIA path: Software BGRA->NV12 conversion
                            var nv12Bytes = ConvertFrameForNvidiaToByte(mon, texture);
                            if (nv12Bytes != null)
                            {
                                OnMonitorNV12Bytes?.Invoke(mon.Index, nv12Bytes, mon.Width, mon.Height, captureTimestamp);
                            }
                        }
                        else
                        {
                            // AMD/Intel path: GPU Video Processor conversion
                            if (mon.ColorConverter == null && mon.Device != null)
                            {
                                mon.ColorConverter = new GpuColorConverter(mon.Device, mon.Width, mon.Height);
                                Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: GpuColorConverter created");
                            }
                            
                            // Keep a copy for static screen (no new frames)
                            if (mon.LastFrame == null && mon.Device != null)
                            {
                                mon.LastFrame = mon.Device.CreateTexture2D(new Texture2DDescription
                                {
                                    Width = (uint)mon.Width,
                                    Height = (uint)mon.Height,
                                    MipLevels = 1,
                                    ArraySize = 1,
                                    Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                                    SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                                    Usage = ResourceUsage.Default,
                                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                                    CPUAccessFlags = CpuAccessFlags.None
                                });
                            }
                            
                            mon.Context?.CopyResource(mon.LastFrame!, texture);
                            
                            var nv12Texture = mon.ColorConverter?.ConvertToTexture(mon.LastFrame!);
                            if (nv12Texture != null)
                            {
                                OnMonitorFrame?.Invoke(mon.Index, nv12Texture, mon.Width, mon.Height, captureTimestamp);
                            }
                        }
                    }
                    finally
                    {
                        desktopResource.Dispose();
                        mon.Duplication.ReleaseFrame();
                    }
                }
                else if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    // No new frame - use cached frame
                    if (mon.IsNvidia)
                    {
                        if (mon.Nv12Buffer != null)
                        {
                            OnMonitorNV12Bytes?.Invoke(mon.Index, mon.Nv12Buffer, mon.Width, mon.Height, captureTimestamp);
                        }
                    }
                    else if (mon.LastFrame != null && mon.ColorConverter != null)
                    {
                        var nv12Texture = mon.ColorConverter.ConvertToTexture(mon.LastFrame);
                        if (nv12Texture != null)
                        {
                            OnMonitorFrame?.Invoke(mon.Index, nv12Texture, mon.Width, mon.Height, captureTimestamp);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index} error: {ex.Message}");
                Thread.Sleep(50);
            }

            // Frame pacing
            var elapsed = sw.ElapsedMilliseconds;
            var sleepTime = frameTimeMs - (int)(elapsed % frameTimeMs);
            if (sleepTime > 0 && sleepTime < frameTimeMs)
            {
                Thread.Sleep(sleepTime);
            }
        }
        
        Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: Capture thread stopped");
    }

    /// <summary>
    /// NVIDIA-specific frame conversion: BGRA -> NV12 bytes (uses monitor's own device)
    /// </summary>
    private byte[]? ConvertFrameForNvidiaToByte(MonitorInfo mon, ID3D11Texture2D bgraTexture)
    {
        if (mon.Device == null || mon.Context == null) return null;
        
        try
        {
            int w = mon.Width;
            int h = mon.Height;
            
            // Lazy init resources using monitor's OWN device
            if (mon.BgraStagingTexture == null)
            {
                mon.BgraStagingTexture = mon.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                    SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read
                });
                
                mon.Nv12Buffer = new byte[w * h * 3 / 2];
                Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: NVIDIA staging texture initialized");
            }
            
            // Use monitor's OWN context (no contention!)
            mon.Context.CopyResource(mon.BgraStagingTexture!, bgraTexture);
            
            var bgraMapped = mon.Context.Map(mon.BgraStagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    byte* bgraSrc = (byte*)bgraMapped.DataPointer;
                    int bgraPitch = (int)bgraMapped.RowPitch;
                    
                    fixed (byte* nv12Dst = mon.Nv12Buffer)
                    {
                        ConvertBgraToNv12Safe(bgraSrc, bgraPitch, nv12Dst, w, w, h);
                    }
                }
            }
            finally
            {
                mon.Context.Unmap(mon.BgraStagingTexture!, 0);
            }
            
            return mon.Nv12Buffer;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index} NVIDIA conversion error: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Software BGRA to NV12 conversion with bounds safety
    /// </summary>
    private static unsafe void ConvertBgraToNv12Safe(byte* bgra, int bgraPitch, byte* nv12, int nv12Pitch, int width, int height)
    {
        width = width & ~1;
        height = height & ~1;
        
        // Y plane
        for (int y = 0; y < height; y++)
        {
            byte* bgraRow = bgra + y * bgraPitch;
            byte* yRow = nv12 + y * nv12Pitch;
            
            for (int x = 0; x < width; x++)
            {
                int bgraIdx = x * 4;
                byte b = bgraRow[bgraIdx + 0];
                byte g = bgraRow[bgraIdx + 1];
                byte r = bgraRow[bgraIdx + 2];
                
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                yRow[x] = (byte)(yVal < 0 ? 0 : (yVal > 255 ? 255 : yVal));
            }
        }
        
        // UV plane
        byte* uvPlane = nv12 + nv12Pitch * height;
        int uvHeight = height / 2;
        int uvWidth = width / 2;
        
        for (int y = 0; y < uvHeight; y++)
        {
            int srcY0 = y * 2;
            int srcY1 = srcY0 + 1;
            byte* bgraRow0 = bgra + srcY0 * bgraPitch;
            byte* bgraRow1 = bgra + srcY1 * bgraPitch;
            byte* uvRow = uvPlane + y * nv12Pitch;
            
            for (int x = 0; x < uvWidth; x++)
            {
                int srcX0 = x * 2;
                int srcX1 = srcX0 + 1;
                int idx00 = srcX0 * 4;
                int idx10 = srcX1 * 4;
                
                int b = (bgraRow0[idx00 + 0] + bgraRow0[idx10 + 0] + bgraRow1[idx00 + 0] + bgraRow1[idx10 + 0]) >> 2;
                int g = (bgraRow0[idx00 + 1] + bgraRow0[idx10 + 1] + bgraRow1[idx00 + 1] + bgraRow1[idx10 + 1]) >> 2;
                int r = (bgraRow0[idx00 + 2] + bgraRow0[idx10 + 2] + bgraRow1[idx00 + 2] + bgraRow1[idx10 + 2]) >> 2;
                
                int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                
                uvRow[x * 2 + 0] = (byte)(u < 0 ? 0 : (u > 255 ? 255 : u));
                uvRow[x * 2 + 1] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
            }
        }
    }

    public void Dispose()
    {
        Stop();
        
        foreach (var mon in Monitors)
        {
            try { mon.Duplication?.Dispose(); } catch { }
            try { mon.LastFrame?.Dispose(); } catch { }
            try { mon.ColorConverter?.Dispose(); } catch { }
            try { mon.BgraStagingTexture?.Dispose(); } catch { }
            try { mon.Context?.Dispose(); } catch { }
            try { mon.Device?.Dispose(); } catch { }
        }
        Monitors.Clear();
    }
}
