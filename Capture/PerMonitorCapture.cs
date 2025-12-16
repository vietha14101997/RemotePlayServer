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
        
        // Per-monitor capture thread
        public Thread? CaptureThread { get; set; }
        public volatile bool Running;

        
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
    
    // public event Action<int, byte[], int, int, long>? OnMonitorNV12Bytes; // UNUSED

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
            
            Console.WriteLine($"[PerMonitorCapture] Monitor {i}: Created D3D11 device (adapter={monitorAdapter.Description.Description})");
            
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
            long loopStart = sw.ElapsedMilliseconds;
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

                // Use a short timeout (5ms) to just check for new frames.
                // If we wait the full frame time (16ms) AND do processing (copy/encode), we exceed the 16ms budget,
                // causing FPS to drop (e.g. 16ms wait + 6ms work = 22ms loop = ~45fps).
                // Our Pacing loop at the bottom handles the rest of the wait to exact 60fps.
                int timeoutMs = 5;
                var result = mon.Duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);
                
                if (result.Success && desktopResource != null)
                {
                    mon.CaptureFrameCount++;
                    
                    try
                    {
                        using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
                        
                        // GPU Video Processor conversion (Unified path for all vendors)
                        if (mon.ColorConverter == null && mon.Device != null)
                        {
                            mon.ColorConverter = new GpuColorConverter(mon.Device, mon.Width, mon.Height);
                            Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: GpuColorConverter created");
                        }
                        
                        // Keep a copy for static screen (no new frames) and for safe conversion (avoids Desktop Duplication issues)
                        // This serves as a "safety firewall" between Desktop Duplication and the encoder/video processor
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
                        else
                        {
                            Console.WriteLine($"[PerMonitorCapture] Mon {mon.Index}: ConvertToTexture returned null!");
                        }
                    }
                    finally
                    {
                        desktopResource.Dispose();
                        mon.Duplication.ReleaseFrame();
                    }
                }
                // PACING STRATEGY:
                // If we got a frame (Success), DXGI has already waited for VSync/Update, so we don't need to sleep.
                // We loop immediately to be ready for the next frame.
                //
                // If we timed out (WaitTimeout), we are sending cached frames.
                // We MUST sleep to avoid a busy loop consuming 100% CPU.
                else if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    // No new frame - use cached frame
                    if (mon.LastFrame != null && mon.ColorConverter != null)
                    {
                        var nv12Texture = mon.ColorConverter.ConvertToTexture(mon.LastFrame);
                        if (nv12Texture != null)
                        {
                            OnMonitorFrame?.Invoke(mon.Index, nv12Texture, mon.Width, mon.Height, captureTimestamp);
                        }
                    }
                }

                // PACING STRATEGY (Updated):
                // We want to stabilize FPS at TargetFps (e.g., 60), but slightly UNDER to prevent client buffering.
                // If we send 60.01 FPS and client runs at 60.00 FPS, buffer grows indefinitely (latency drift).
                // Aiming for 59.9 FPS ensures the client drain rate > send rate => Zero Latency.
                
                var loopDuration = sw.ElapsedMilliseconds - loopStart;
                // Add 0.5ms safety bias to ensure we never over-shoot speed
                var timeToSleep = (frameTimeMs + 0.5) - loopDuration;

                if (timeToSleep > 0)
                {
                    // HIGH PRECISION PACING:
                    // Standard Thread.Sleep() has ~15ms resolution on Windows, which causes massive jitter
                    // and FPS drops (e.g. asking for 5ms sleep -> getting 15ms -> 40fps).
                    // We use pure SpinWait for the remaining time to guarantee rock-solid 60fps.
                    // This uses slightly more CPU but is required for low-latency streaming.
                    
                    while ((sw.ElapsedMilliseconds - loopStart) < (frameTimeMs + 0.1)) // +0.1 margin
                    {
                        Thread.SpinWait(10); // Lightweight spin
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index} error: {ex.Message}");
                Thread.Sleep(50);
            }
        }

        
        Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: Capture thread stopped");
    }


    public void Dispose()
    {
        Stop();
        
        foreach (var mon in Monitors)
        {
            try { mon.Duplication?.Dispose(); } catch { }
            try { mon.LastFrame?.Dispose(); } catch { }
            try { mon.ColorConverter?.Dispose(); } catch { }
            try { mon.Context?.Dispose(); } catch { }
            try { mon.Device?.Dispose(); } catch { }
        }
        Monitors.Clear();
    }
}
