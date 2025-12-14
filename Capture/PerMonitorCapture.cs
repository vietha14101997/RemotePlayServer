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
    }

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
        primaryAdapter.Dispose();
        Console.WriteLine("[PerMonitorCapture] D3D11 device created");

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
                
                // Create color converter for NV12
                info.ColorConverter = new GpuColorConverter(_device, mon.w, mon.h);
                
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
                    if (mon.Duplication == null || mon.ColorConverter == null) continue;

                    var result = mon.Duplication.AcquireNextFrame(5, out var frameInfo, out var desktopResource);
                    
                    if (result.Success && desktopResource != null)
                    {
                        try
                        {
                            using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
                            
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
                                    BindFlags = BindFlags.None,
                                    CPUAccessFlags = CpuAccessFlags.None
                                });
                            }
                            _context.CopyResource(mon.LastFrame, texture);
                            
                            // Convert to NV12 on GPU
                            var nv12Texture = mon.ColorConverter.ConvertToTexture(texture);
                            if (nv12Texture != null)
                            {
                                OnMonitorFrame?.Invoke(mon.Index, nv12Texture, mon.Width, mon.Height, captureTimestamp);
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
                        if (mon.LastFrame != null && mon.ColorConverter != null)
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

    public void Dispose()
    {
        Stop();
        
        foreach (var mon in Monitors)
        {
            try { mon.Duplication?.Dispose(); } catch { }
            try { mon.LastFrame?.Dispose(); } catch { }
            try { mon.ColorConverter?.Dispose(); } catch { }
        }
        Monitors.Clear();
        
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
