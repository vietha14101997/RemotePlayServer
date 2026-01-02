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
        public ID3D11Texture2D? LastNV12Frame { get; set; } // Cached NV12 frame to avoid re-conversion on Timeout
        public GpuColorConverter? ColorConverter { get; set; }
        
        // Per-monitor capture thread
        public Thread? CaptureThread { get; set; }
        public volatile bool Running;

        
        // FPS tracking for diagnostics
        public long CaptureFrameCount;
        public long LastFpsLogTime;
        public long LastFpsLogFrameCount;

        // Rate limiting - prevent queue buildup during high activity (e.g., dragging windows)
        public long LastSentTime;
        public long RateLimitedFrames;
    }
    
    private volatile bool _running;
    private string? _preferredGpu;

    // Frame synchronization across monitors
    private Barrier? _captureBarrier;
    private long _syncedTimestamp; // Shared timestamp for all monitors in a frame (use Interlocked for access)

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

        // Count active monitors (have device and duplication)
        int activeMonitors = 0;
        foreach (var mon in Monitors)
        {
            if (mon.Device != null && mon.Duplication != null)
                activeMonitors++;
        }

        // Create barrier for frame synchronization across all active monitors
        // The post-phase action sets the shared timestamp for the frame
        if (activeMonitors > 1)
        {
            _captureBarrier = new Barrier(activeMonitors, (b) =>
            {
                // This runs once after all threads reach the barrier
                // Set shared timestamp so all monitors use the same frame timestamp
                Interlocked.Exchange(ref _syncedTimestamp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            });
            Console.WriteLine($"[PerMonitorCapture] Created frame sync barrier for {activeMonitors} monitors");
        }

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

        Console.WriteLine($"[PerMonitorCapture] Started {activeMonitors} parallel capture threads (synchronized)");
    }

    public void Stop()
    {
        _running = false;

        // Stop all monitor threads
        foreach (var mon in Monitors)
        {
            mon.Running = false;
        }

        // Dispose barrier to unblock any waiting threads
        try { _captureBarrier?.Dispose(); } catch { }
        _captureBarrier = null;

        // Wait for threads to finish
        foreach (var mon in Monitors)
        {
            if (mon.CaptureThread != null && mon.CaptureThread.IsAlive)
            {
               try { mon.CaptureThread.Join(1000); } catch {}
            }
        }

        Console.WriteLine("[PerMonitorCapture] All capture threads stopped");
    }

    /// <summary>
    /// Capture loop for a single monitor - runs in its own thread with its own D3D11 device
    /// Uses barrier synchronization to ensure all monitors capture at the same moment.
    /// </summary>
    private void CaptureLoopForMonitor(MonitorInfo mon)
    {
        int frameTimeMs = 1000 / TargetFps;
        Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: Capture thread started @ {TargetFps}fps (barrier sync enabled: {_captureBarrier != null})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        mon.LastFpsLogTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        mon.LastFpsLogFrameCount = 0;
        mon.CaptureFrameCount = 0;

        while (mon.Running && _running)
        {
            long loopStart = sw.ElapsedMilliseconds;
            try
            {
                // FRAME SYNC: Wait for all monitors to be ready before capturing
                // This ensures all monitors capture within microseconds of each other
                long captureTimestamp;
                if (_captureBarrier != null)
                {
                    try
                    {
                        _captureBarrier.SignalAndWait(); // Wait for all monitors
                        captureTimestamp = Interlocked.Read(ref _syncedTimestamp); // Use shared timestamp
                    }
                    catch (BarrierPostPhaseException)
                    {
                        captureTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // Barrier disposed, exit loop
                    }
                }
                else
                {
                    captureTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                // FPS logging every 3 seconds
                long timeSinceLastLog = captureTimestamp - mon.LastFpsLogTime;
                if (timeSinceLastLog >= 3000)
                {
                    long framesSinceLastLog = mon.CaptureFrameCount - mon.LastFpsLogFrameCount;
                    double fps = framesSinceLastLog * 1000.0 / timeSinceLastLog;
                    long rateLimited = Interlocked.Exchange(ref mon.RateLimitedFrames, 0); // Read and reset
                    Console.WriteLine($"[Capture FPS] Mon{mon.Index}: {fps:F1} fps (captured {framesSinceLastLog} in {timeSinceLastLog}ms, rate-limited={rateLimited})");
                    mon.LastFpsLogTime = captureTimestamp;
                    mon.LastFpsLogFrameCount = mon.CaptureFrameCount;
                }

                if (mon.Duplication == null) goto Pacing;

                // RATE LIMITING CHECK (BEFORE acquiring frame)
                // This is the key fix: don't even try to acquire frames too frequently
                long timeSinceLastSent = loopStart - mon.LastSentTime;
                bool canSendFrame = mon.LastSentTime == 0 || timeSinceLastSent >= frameTimeMs;

                // Use short timeout - we just want to check for new frames and release DXGI's internal queue
                int timeoutMs = canSendFrame ? 5 : 1; // Even shorter timeout when rate limited
                var result = mon.Duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);

                if (result.Success && desktopResource != null)
                {
                    mon.CaptureFrameCount++;

                    try
                    {
                        using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();

                        // Always cache the latest frame content (fast GPU copy)
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

                        // Only convert and send if rate limiting allows
                        if (canSendFrame)
                        {
                            // GPU Video Processor conversion
                            if (mon.ColorConverter == null && mon.Device != null)
                            {
                                mon.ColorConverter = new GpuColorConverter(mon.Device, mon.Width, mon.Height);
                                Console.WriteLine($"[PerMonitorCapture] Monitor {mon.Index}: GpuColorConverter created");
                            }

                            var nv12Texture = mon.ColorConverter?.ConvertToTexture(mon.LastFrame!);
                            mon.LastNV12Frame = nv12Texture;

                            if (nv12Texture != null)
                            {
                                mon.LastSentTime = loopStart;
                                OnMonitorFrame?.Invoke(mon.Index, nv12Texture, mon.Width, mon.Height, captureTimestamp);
                            }
                        }
                        else
                        {
                            // Rate limited - frame cached but not sent
                            mon.RateLimitedFrames++;
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
                    // No new frame from DXGI - send cached frame if rate limiting allows
                    if (canSendFrame && mon.LastNV12Frame != null)
                    {
                        mon.LastSentTime = loopStart;
                        OnMonitorFrame?.Invoke(mon.Index, mon.LastNV12Frame, mon.Width, mon.Height, captureTimestamp);
                    }
                }

                Pacing:
                // STRICT PACING: Always wait until frame time has passed
                // This ensures we never exceed TargetFps, preventing queue buildup
                while ((sw.ElapsedMilliseconds - loopStart) < frameTimeMs)
                {
                    Thread.SpinWait(10);
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
