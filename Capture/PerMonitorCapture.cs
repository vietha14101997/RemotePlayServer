#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Captures individual monitors separately (not combined).
/// Each monitor produces its own NV12 texture stream for multi-track encoding.
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
        public IDXGIOutputDuplication? Duplication { get; set; }
        public ID3D11Texture2D? LastFrame { get; set; }
        public GpuColorConverter? ColorConverter { get; set; }
        
        // For NVIDIA software conversion path (NV12 bytes, not texture)
        public ID3D11Texture2D? BgraStagingTexture { get; set; }
        public byte[]? Nv12Buffer { get; set; }
    }
    
    // NVIDIA needs special handling - Video Processor doesn't work with Desktop Duplication
    private bool _isNvidia = false;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    
    private volatile bool _running;
    private Thread? _captureThread;

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
    /// Expose D3D11 device for encoder initialization
    /// </summary>
    public ID3D11Device Device => _device;

    public PerMonitorCapture(List<(IntPtr hmon, string name, int w, int h)> monitors, int targetFps = 30, string? preferredGpu = null)
    {
        if (monitors == null || monitors.Count == 0)
            throw new ArgumentException("At least one monitor required");

        MonitorCount = monitors.Count;
        TargetFps = Math.Clamp(targetFps, 10, 60);

        Console.WriteLine($"[PerMonitorCapture] Initializing {MonitorCount} monitors @ {TargetFps}fps");

        // Find adapter
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        
        IDXGIAdapter1? primaryAdapter = null;
        
        // Try preferred GPU
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
                    primaryAdapter = adapter;
                    Console.WriteLine($"[PerMonitorCapture] Using preferred adapter: {adapter.Description.Description}");
                    break;
                }
                adapter.Dispose();
            }
        }
        
        // Fallback: find by first monitor
        if (primaryAdapter == null)
        {
            for (uint ai = 0; ; ai++)
            {
                if (factory.EnumAdapters1(ai, out var adapter).Failure) break;
                for (uint oi = 0; ; oi++)
                {
                    if (adapter.EnumOutputs(oi, out var output).Failure) break;
                    if (output.Description.Monitor == monitors[0].hmon)
                    {
                        primaryAdapter = adapter;
                        Console.WriteLine($"[PerMonitorCapture] Using adapter: {adapter.Description.Description}");
                        output.Dispose();
                        break;
                    }
                    output.Dispose();
                }
                if (primaryAdapter != null) break;
                adapter.Dispose();
            }
        }

        if (primaryAdapter == null)
            throw new InvalidOperationException("Could not find adapter for monitors");

        // Create D3D11 device
        var levels = new[] { FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            primaryAdapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels,
            out _device,
            out _context
        );
        // Detect if NVIDIA for special handling
        var adapterName = primaryAdapter.Description.Description.ToUpperInvariant();
        _isNvidia = adapterName.Contains("NVIDIA") || adapterName.Contains("GEFORCE") || 
                    adapterName.Contains("GTX") || adapterName.Contains("RTX");
        primaryAdapter.Dispose();
        Console.WriteLine($"[PerMonitorCapture] D3D11 device created (NVIDIA={_isNvidia})");

        // Setup each monitor
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

            // Find output duplication
            IDXGIOutput? targetOutput = null;
            for (uint ai = 0; ; ai++)
            {
                if (factory.EnumAdapters1(ai, out var adapter).Failure) break;
                for (uint oi = 0; ; oi++)
                {
                    if (adapter.EnumOutputs(oi, out var output).Failure) break;
                    if (output.Description.Monitor == mon.hmon)
                    {
                        targetOutput = output;
                        break;
                    }
                    output.Dispose();
                }
                adapter.Dispose();
                if (targetOutput != null) break;
            }

            if (targetOutput != null)
            {
                using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
                targetOutput.Dispose();
                info.Duplication = output1.DuplicateOutput(_device);
                
                // NOTE: Don't create GpuColorConverter here - use lazy init in CaptureLoop
                // This avoids issues with Video Processor when display config changes after init
                
                Console.WriteLine($"[PerMonitorCapture] Monitor {i}: {mon.name} {mon.w}x{mon.h}");
            }
            else
            {
                Console.WriteLine($"[PerMonitorCapture] Warning: No output for {mon.name}");
            }

            Monitors.Add(info);
        }

        Console.WriteLine($"[PerMonitorCapture] Initialized {Monitors.Count} monitors");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "PerMonitor-Capture"
        };
        _captureThread.Start();
        Console.WriteLine("[PerMonitorCapture] Capture started");
    }

    public void Stop()
    {
        _running = false;
        _captureThread?.Join(1000);
        Console.WriteLine("[PerMonitorCapture] Capture stopped");
    }

    private void CaptureLoop()
    {
        int frameTimeMs = 1000 / TargetFps;
        Console.WriteLine($"[PerMonitorCapture] Target {TargetFps}fps, interval {frameTimeMs}ms");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (_running)
        {
            try
            {
                long captureTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Capture each monitor
                foreach (var mon in Monitors)
                {
                    if (mon.Duplication == null) continue;

                    var result = mon.Duplication.AcquireNextFrame(5, out var frameInfo, out var desktopResource);
                    
                    if (result.Success && desktopResource != null)
                    {
                        try
                        {
                            using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
                            
                            // NVIDIA path: Use software conversion to NV12 bytes (not texture!)
                            // This matches how Cluster mode works with NVIDIA - bytes path is compatible
                            if (_isNvidia)
                            {
                                var nv12Bytes = ConvertFrameForNvidiaToByte(mon, texture);
                                if (nv12Bytes != null)
                                {
                                    OnMonitorNV12Bytes?.Invoke(mon.Index, nv12Bytes, mon.Width, mon.Height, captureTimestamp);
                                }
                            }
                            else
                            {
                                // AMD/Intel path: Use GpuColorConverter with Video Processor
                                if (mon.ColorConverter == null)
                                {
                                    mon.ColorConverter = new GpuColorConverter(_device, mon.Width, mon.Height);
                                    Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: GpuColorConverter created (lazy init)");
                                }
                                
                                // Save a copy for use when screen is static (no new frames)
                                if (mon.LastFrame == null)
                                {
                                    mon.LastFrame = _device.CreateTexture2D(new Texture2DDescription
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
                                _context.CopyResource(mon.LastFrame, texture);
                                
                                var nv12Texture = mon.ColorConverter.ConvertToTexture(mon.LastFrame);
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
                        // No new frame - use last frame if available (screen is static)
                        if (_isNvidia)
                        {
                            // NVIDIA: Use cached NV12 bytes
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PerMonitorCapture] Error: {ex.Message}");
                Thread.Sleep(100);
            }

            // Frame pacing
            var elapsed = sw.ElapsedMilliseconds;
            var sleepTime = frameTimeMs - (int)(elapsed % frameTimeMs);
            if (sleepTime > 0 && sleepTime < frameTimeMs)
            {
                Thread.Sleep(sleepTime);
            }
        }
    }

    /// <summary>
    /// NVIDIA-specific frame conversion: BGRA -> NV12 bytes
    /// This matches how Cluster mode works with NVIDIA - uses bytes path which is compatible
    /// </summary>
    private byte[]? ConvertFrameForNvidiaToByte(MonitorInfo mon, ID3D11Texture2D bgraTexture)
    {
        try
        {
            int w = mon.Width;
            int h = mon.Height;
            
            // Lazy init resources
            if (mon.BgraStagingTexture == null)
            {
                mon.BgraStagingTexture = _device.CreateTexture2D(new Texture2DDescription
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
                
                mon.Nv12Buffer = new byte[w * h * 3 / 2]; // NV12 size = Y plane + UV plane
                
                Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: NVIDIA NV12 bytes conversion initialized (w={w}, h={h})");
            }
            
            // Copy BGRA to staging
            _context.CopyResource(mon.BgraStagingTexture!, bgraTexture);
            
            // Map BGRA staging for read and convert to NV12 bytes
            var bgraMapped = _context.Map(mon.BgraStagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    byte* bgraSrc = (byte*)bgraMapped.DataPointer;
                    int bgraPitch = (int)bgraMapped.RowPitch;
                    
                    fixed (byte* nv12Dst = mon.Nv12Buffer)
                    {
                        // For byte array, use width as pitch (contiguous memory)
                        ConvertBgraToNv12Safe(bgraSrc, bgraPitch, nv12Dst, w, w, h);
                    }
                }
            }
            finally
            {
                _context.Unmap(mon.BgraStagingTexture!, 0);
            }
            
            return mon.Nv12Buffer;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PerMonitorCapture] NVIDIA conversion error: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Software BGRA to NV12 conversion with bounds safety
    /// </summary>
    private static unsafe void ConvertBgraToNv12Safe(byte* bgra, int bgraPitch, byte* nv12, int nv12Pitch, int width, int height)
    {
        // Ensure even dimensions
        width = width & ~1;
        height = height & ~1;
        
        // Y plane - write Y values row by row
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
                
                // BT.601 Y conversion
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                yRow[x] = (byte)(yVal < 0 ? 0 : (yVal > 255 ? 255 : yVal));
            }
        }
        
        // UV plane starts after Y plane
        // For NV12 staging texture, UV plane is at pitch * height offset
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
                
                // Get 2x2 block pixels
                int idx00 = srcX0 * 4;
                int idx10 = srcX1 * 4;
                
                // Average 2x2 block
                int b = (bgraRow0[idx00 + 0] + bgraRow0[idx10 + 0] + bgraRow1[idx00 + 0] + bgraRow1[idx10 + 0]) >> 2;
                int g = (bgraRow0[idx00 + 1] + bgraRow0[idx10 + 1] + bgraRow1[idx00 + 1] + bgraRow1[idx10 + 1]) >> 2;
                int r = (bgraRow0[idx00 + 2] + bgraRow0[idx10 + 2] + bgraRow1[idx00 + 2] + bgraRow1[idx10 + 2]) >> 2;
                
                // BT.601 U, V conversion
                int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                
                uvRow[x * 2 + 0] = (byte)(u < 0 ? 0 : (u > 255 ? 255 : u)); // U
                uvRow[x * 2 + 1] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v)); // V
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
        }
        Monitors.Clear();
        
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
