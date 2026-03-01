#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Capture;

/// <summary>
/// Windows Multimedia Timer for high-resolution Sleep.
/// Reduces Sleep granularity from ~15ms to ~1ms.
/// </summary>
internal static class MultimediaTimer
{
    [DllImport("winmm.dll", SetLastError = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    private static bool _initialized;
    private static readonly object _lock = new();

    public static void Begin()
    {
        lock (_lock)
        {
            if (!_initialized)
            {
                timeBeginPeriod(1); // Set timer resolution to 1ms
                _initialized = true;
                Logger.Info("[MultimediaTimer] Timer resolution set to 1ms");
            }
        }
    }

    public static void End()
    {
        lock (_lock)
        {
            if (_initialized)
            {
                timeEndPeriod(1);
                _initialized = false;
                Logger.Info("[MultimediaTimer] Timer resolution restored");
            }
        }
    }
}

/// <summary>
/// Captures individual monitors separately (not combined).
/// Each monitor has its own D3D11 Device and capture thread for TRUE PARALLEL capture.
/// This eliminates context serialization bottleneck when capturing multiple monitors.
/// </summary>
public sealed class PerMonitorCapture : IDisposable
{
    public readonly int MonitorCount;
    private volatile int _targetFps;
    public int TargetFps => _targetFps;
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

        // Desktop Duplication recovery state
        public DateTime LastAccessLostTime;
        public int AccessLostCount;
        public bool RecreatingDuplication;

        // Per-monitor pause state
        public volatile bool Paused;

        // DXGI Cursor capture state
        public byte[]? LastCursorBuffer;
        public OutduplPointerShapeInfo? LastCursorShapeInfo;
        public long LastCursorShapeId; // Unique ID for cursor shape (incremented on shape change)
        public OutduplPointerPosition LastCursorPosition; // Cached position (updated only when Visible=true)
    }
    
    private volatile bool _running;
    private string? _preferredGpu;

    // Frame synchronization across monitors
    private Barrier? _captureBarrier;
    private long _syncedTimestamp; // Shared timestamp for all monitors in a frame (use Interlocked for access)

    // Post-encode barrier: sync all monitors after encoding, before sending RTP
    // Prevents consistent jitter asymmetry where one track's packets always arrive first
    private Barrier? _postEncodeBarrier;

    /// <summary>
    /// Fired once after all monitors complete encoding for a frame.
    /// The post-phase action sends all buffered frames in alternating track order.
    /// </summary>
    public event Action? OnPostEncodeSync;

    /// <summary>
    /// One-shot callback fired in the capture barrier's post-phase action.
    /// Used for Phase 3 activation — ensures all monitors see the state change
    /// at the same barrier cycle (no race between capture threads).
    /// Automatically cleared after firing.
    /// </summary>
    public volatile Action? OnNextBarrierSync;

    // Track which monitor currently has the cursor (shared across all capture threads)
    // When a monitor reports Visible=true, it becomes the active cursor monitor
    // Only the active cursor monitor fires cursor events (prevents duplicate events)
    private volatile int _activeCursorMonitor = -1;

    // Global cursor shape (shared across all monitors - cursor looks the same on all monitors)
    // This is needed because cursor shape is only reported by the monitor where shape changed
    private byte[]? _globalCursorBuffer;
    private OutduplPointerShapeInfo? _globalCursorShapeInfo;
    private long _globalCursorShapeId;
    private readonly object _globalCursorLock = new();

    /// <summary>
    /// Callback for each monitor's NV12 texture frame.
    /// Parameters: monitorIndex, nv12Texture, width, height, timestamp
    /// </summary>
    public event Action<int, ID3D11Texture2D, int, int, long>? OnMonitorFrame;

    /// <summary>
    /// Callback for BGRA texture frame (for encoders that accept BGRA directly).
    /// When set, bypasses color conversion for better GPU efficiency.
    /// Parameters: monitorIndex, bgraTexture, width, height, timestamp
    /// </summary>
    public event Action<int, ID3D11Texture2D, int, int, long>? OnMonitorFrameBgra;

    /// <summary>
    /// When true, sends BGRA frames directly via OnMonitorFrameBgra instead of converting to NV12.
    /// Set this after encoder initialization if encoder supports BGRA input.
    /// </summary>
    public bool UseBgraMode { get; set; } = false;

    /// <summary>
    /// Callback for cursor updates from DXGI Desktop Duplication.
    /// Parameters: monitorIndex, cursorBuffer (raw pixel data), shapeInfo (dimensions, type, hotspot), position (screen coords, visibility), shapeId (unique ID)
    /// Only fired when cursor is visible. shapeId changes when cursor shape changes.
    /// </summary>
    public event Action<int, byte[], OutduplPointerShapeInfo, OutduplPointerPosition, long>? OnCursorUpdate;

    // Global cursor shape ID — content-based hash so same visual cursor always has same ID.
    // Prevents duplicate image sends when DXGI re-reports identical cursor bitmaps.

    /// <summary>
    /// Compute a stable content-based hash for cursor bitmap data.
    /// Same visual cursor (same pixels + shape info) → same hash → same cursorId.
    /// Uses FNV-1a 64-bit hash for speed (cursor buffers are typically 4KB).
    /// </summary>
    private static long ComputeCursorContentHash(byte[] buffer, OutduplPointerShapeInfo shapeInfo)
    {
        // FNV-1a 64-bit
        const ulong FNV_OFFSET = 14695981039346656037UL;
        const ulong FNV_PRIME = 1099511628211UL;

        ulong hash = FNV_OFFSET;

        // Include shape metadata in hash
        hash ^= (ulong)(int)shapeInfo.Type;  hash *= FNV_PRIME;
        hash ^= (ulong)shapeInfo.Width;      hash *= FNV_PRIME;
        hash ^= (ulong)shapeInfo.Height;     hash *= FNV_PRIME;
        hash ^= (ulong)shapeInfo.HotSpot.X;  hash *= FNV_PRIME;
        hash ^= (ulong)shapeInfo.HotSpot.Y;  hash *= FNV_PRIME;

        // Hash bitmap data (process 8 bytes at a time for speed)
        int i = 0;
        int len = buffer.Length;
        for (; i + 7 < len; i += 8)
        {
            ulong block = BitConverter.ToUInt64(buffer, i);
            hash ^= block;
            hash *= FNV_PRIME;
        }
        // Remaining bytes
        for (; i < len; i++)
        {
            hash ^= buffer[i];
            hash *= FNV_PRIME;
        }

        // Return as positive long (avoid negative cursorId confusion)
        return (long)(hash & 0x7FFFFFFFFFFFFFFFUL);
    }

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

    /// <summary>
    /// Pause capture for a specific monitor.
    /// Paused monitors skip frame acquisition and don't fire events.
    /// </summary>
    public void PauseMonitor(int monitorIndex)
    {
        if (monitorIndex >= 0 && monitorIndex < Monitors.Count)
        {
            Monitors[monitorIndex].Paused = true;
            Logger.Info($"[PerMonitorCapture] Monitor {monitorIndex}: PAUSED");
        }
    }

    /// <summary>
    /// Resume capture for a specific monitor.
    /// </summary>
    public void ResumeMonitor(int monitorIndex)
    {
        if (monitorIndex >= 0 && monitorIndex < Monitors.Count)
        {
            Monitors[monitorIndex].Paused = false;
            Logger.Info($"[PerMonitorCapture] Monitor {monitorIndex}: RESUMED");
        }
    }

    /// <summary>
    /// Check if a monitor is paused.
    /// </summary>
    public bool IsMonitorPaused(int monitorIndex)
    {
        if (monitorIndex >= 0 && monitorIndex < Monitors.Count)
            return Monitors[monitorIndex].Paused;
        return false;
    }

    /// <summary>
    /// Recreate Desktop Duplication for a monitor after ACCESS_LOST error.
    /// This happens when desktop mode changes (resize, resolution change, etc.)
    /// </summary>
    private bool RecreateDuplication(MonitorInfo mon)
    {
        if (mon.Device == null) return false;

        try
        {
            mon.RecreatingDuplication = true;

            // Dispose old duplication
            try { mon.Duplication?.Dispose(); } catch { }
            mon.Duplication = null;

            // Find adapter and output for this monitor by HMON
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint ai = 0; ; ai++)
            {
                if (factory.EnumAdapters1(ai, out var adapter).Failure) break;

                for (uint oi = 0; ; oi++)
                {
                    if (adapter.EnumOutputs(oi, out var output).Failure) break;

                    if (output.Description.Monitor == mon.HMon)
                    {
                        // Found the output for this monitor
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        output.Dispose();

                        mon.Duplication = output1.DuplicateOutput(mon.Device);
                        Logger.Info($"[PerMonitorCapture] Monitor {mon.Index}: Duplication recreated successfully");

                        adapter.Dispose();
                        mon.RecreatingDuplication = false;
                        return true;
                    }
                    output.Dispose();
                }
                adapter.Dispose();
            }

            Logger.Error($"[PerMonitorCapture] Monitor {mon.Index}: Failed to find output for HMON");
            mon.RecreatingDuplication = false;
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[PerMonitorCapture] Monitor {mon.Index}: RecreateDuplication failed: {ex.Message}");
            mon.RecreatingDuplication = false;
            return false;
        }
    }

    public PerMonitorCapture(List<(IntPtr hmon, string name, int w, int h)> monitors, int targetFps = 30, string? preferredGpu = null)
    {
        if (monitors == null || monitors.Count == 0)
            throw new ArgumentException("At least one monitor required");

        MonitorCount = monitors.Count;
        _targetFps = Math.Clamp(targetFps, 10, 60);
        _preferredGpu = preferredGpu;

        Logger.Info($"[PerMonitorCapture] Initializing {MonitorCount} monitors @ {TargetFps}fps (PARALLEL MODE - separate D3D11 devices)");

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
                Logger.Warn($"[PerMonitorCapture] Warning: No adapter for monitor {i} ({mon.name})");
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
            
            Logger.Info($"[PerMonitorCapture] Monitor {i}: Created D3D11 device (adapter={monitorAdapter.Description.Description})");
            
            // Create output duplication using THIS monitor's device
            if (targetOutput != null)
            {
                using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
                targetOutput.Dispose();
                info.Duplication = output1.DuplicateOutput(device);
                Logger.Info($"[PerMonitorCapture] Monitor {i}: {mon.name} {mon.w}x{mon.h} - duplication ready");
            }
            
            monitorAdapter.Dispose();
            Monitors.Add(info);
        }

        Logger.Info($"[PerMonitorCapture] Initialized {Monitors.Count} monitors with SEPARATE D3D11 devices");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        // Enable high-resolution timer for precise Sleep()
        MultimediaTimer.Begin();

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

                // One-shot barrier-synced callback (Phase 3 activation, etc.)
                // Ensures all monitors see state changes at the same barrier cycle.
                var action = OnNextBarrierSync;
                if (action != null)
                {
                    OnNextBarrierSync = null;
                    try { action(); }
                    catch (Exception ex) { Logger.Error($"[PerMonitorCapture] OnNextBarrierSync error: {ex.Message}"); }
                }
            });

            // Post-encode barrier: after all tracks finish encoding, one designated thread
            // (mon.Index == 0) flushes all buffered frames in alternating track order.
            // No post-phase action — flush runs AFTER SignalAndWait returns, avoiding
            // stalling the barrier while SRTP send is in progress.
            _postEncodeBarrier = new Barrier(activeMonitors);

            Logger.Info($"[PerMonitorCapture] Created frame sync barrier for {activeMonitors} monitors (capture + post-encode)");
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

        Logger.Info($"[PerMonitorCapture] Started {activeMonitors} parallel capture threads (synchronized)");
    }

    public void Stop()
    {
        _running = false;

        // Stop all monitor threads
        foreach (var mon in Monitors)
        {
            mon.Running = false;
        }

        // Dispose barriers to unblock any waiting threads
        try { _captureBarrier?.Dispose(); } catch { }
        _captureBarrier = null;
        try { _postEncodeBarrier?.Dispose(); } catch { }
        _postEncodeBarrier = null;

        // Wait for threads to finish
        foreach (var mon in Monitors)
        {
            if (mon.CaptureThread != null && mon.CaptureThread.IsAlive)
            {
               try { mon.CaptureThread.Join(1000); } catch {}
            }
        }

        // Restore default timer resolution
        MultimediaTimer.End();

        Logger.Info("[PerMonitorCapture] All capture threads stopped");
    }

    /// <summary>
    /// Dynamically change the target FPS during capture.
    /// The change takes effect immediately on all capture threads.
    /// </summary>
    /// <param name="fps">New target FPS (will be clamped to 10-60 range)</param>
    public void SetTargetFps(int fps)
    {
        int newFps = Math.Clamp(fps, 10, 60);
        int oldFps = _targetFps;
        _targetFps = newFps;
        Logger.Info($"[PerMonitorCapture] Target FPS changed: {oldFps} → {newFps}");
    }

    /// <summary>
    /// Capture loop for a single monitor - runs in its own thread with its own D3D11 device
    /// Uses barrier synchronization to ensure all monitors capture at the same moment.
    /// </summary>
    private void CaptureLoopForMonitor(MonitorInfo mon)
    {
        // With MultimediaTimer (1ms resolution), Sleep is now precise enough for all FPS
        // Keep 3ms spin buffer for sub-millisecond precision at frame boundaries
        const int sleepThresholdMs = 3;
        Logger.Info($"[PerMonitorCapture] Monitor {mon.Index}: Capture thread started @ {TargetFps}fps, sleepThreshold={sleepThresholdMs}ms (barrier sync: {_captureBarrier != null})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        mon.LastFpsLogTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        mon.LastFpsLogFrameCount = 0;
        mon.CaptureFrameCount = 0;

        while (mon.Running && _running)
        {
            long loopStart = sw.ElapsedMilliseconds;
            // Calculate frame time dynamically to support runtime FPS changes
            int frameTimeMs = 1000 / _targetFps;
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

                // FPS logging every 10 seconds
                long timeSinceLastLog = captureTimestamp - mon.LastFpsLogTime;
                if (timeSinceLastLog >= 10000)
                {
                    long framesSinceLastLog = mon.CaptureFrameCount - mon.LastFpsLogFrameCount;
                    double fps = framesSinceLastLog * 1000.0 / timeSinceLastLog;
                    long rateLimited = Interlocked.Exchange(ref mon.RateLimitedFrames, 0); // Read and reset
                    Logger.Debug($"[Capture FPS] Mon{mon.Index}: {fps:F1} fps (captured {framesSinceLastLog} in {timeSinceLastLog}ms, rate-limited={rateLimited})");
                    mon.LastFpsLogTime = captureTimestamp;
                    mon.LastFpsLogFrameCount = mon.CaptureFrameCount;
                }

                if (mon.Duplication == null) goto PostEncode;

                // PAUSE CHECK: Skip frame acquisition entirely when monitor is paused
                if (mon.Paused) goto PostEncode;

                // RATE LIMITING CHECK (BEFORE acquiring frame)
                // Rate limiting controls SENDING, not ACQUIRING - always try to get the latest frame
                long timeSinceLastSent = loopStart - mon.LastSentTime;
                bool canSendFrame = mon.LastSentTime == 0 || timeSinceLastSent >= frameTimeMs;

                // Use consistent timeout for frame acquisition regardless of rate-limiting
                // Previously: 1ms when rate-limited caused DXGI to always timeout, dropping FPS to 0
                // Fix: Always use reasonable timeout to properly acquire and cache frames
                const int ACQUIRE_TIMEOUT_MS = 8; // ~120fps max check rate, allows proper frame caching
                int timeoutMs = ACQUIRE_TIMEOUT_MS;
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

                        // ===== DXGI Cursor Capture =====
                        // Capture cursor from Desktop Duplication API (more accurate than GDI+)
                        try
                        {
                            if (mon.Duplication != null)
                            {
                                // Check if cursor shape changed (PointerShapeBufferSize > 0)
                                // Cursor shape is GLOBAL - same cursor on all monitors
                                if (frameInfo.PointerPosition.Visible && frameInfo.PointerShapeBufferSize > 0)
                                {
                                    // Cursor shape changed - fetch new shape and store globally
                                    var cursorBuffer = new byte[frameInfo.PointerShapeBufferSize];
                                    var handle = GCHandle.Alloc(cursorBuffer, GCHandleType.Pinned);
                                    try
                                    {
                                        var shapeResult = mon.Duplication.GetFramePointerShape(
                                            frameInfo.PointerShapeBufferSize,
                                            handle.AddrOfPinnedObject(),
                                            out var bufferSizeRequired,
                                            out var shapeInfo
                                        );

                                        if (shapeResult.Success)
                                        {
                                            // Store cursor shape globally (shared across all monitors)
                                            // Use content-based hash so same visual cursor → same ID
                                            var contentHash = ComputeCursorContentHash(cursorBuffer, shapeInfo);
                                            lock (_globalCursorLock)
                                            {
                                                _globalCursorBuffer = cursorBuffer;
                                                _globalCursorShapeInfo = shapeInfo;
                                                _globalCursorShapeId = contentHash;
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        handle.Free();
                                    }
                                }

                                // DXGI PointerPosition.Visible meaning:
                                // - Visible=true: cursor MOVED on this monitor this frame, position is valid
                                // - Visible=false: no cursor movement on this monitor this frame
                                //
                                // Strategy: Use _activeCursorMonitor to track which monitor has the cursor
                                // When Visible=true, this monitor becomes the active cursor monitor
                                // Only the active monitor fires cursor events (with cached position)

                                if (frameInfo.PointerPosition.Visible)
                                {
                                    // Cursor moved on this monitor - this is now the active cursor monitor
                                    _activeCursorMonitor = mon.Index;
                                    mon.LastCursorPosition = new OutduplPointerPosition
                                    {
                                        Position = frameInfo.PointerPosition.Position,
                                        Visible = true
                                    };
                                }

                                // Fire cursor update event only if this is the active cursor monitor
                                // Use GLOBAL cursor shape (shared across monitors)
                                byte[]? cursorBuf;
                                OutduplPointerShapeInfo? shapeInf;
                                long shapeIdCopy;
                                lock (_globalCursorLock)
                                {
                                    cursorBuf = _globalCursorBuffer;
                                    shapeInf = _globalCursorShapeInfo;
                                    shapeIdCopy = _globalCursorShapeId;
                                }

                                if (_activeCursorMonitor == mon.Index &&
                                    cursorBuf != null &&
                                    shapeInf.HasValue &&
                                    mon.LastCursorPosition.Visible)
                                {
                                    OnCursorUpdate?.Invoke(
                                        mon.Index,
                                        cursorBuf,
                                        shapeInf.Value,
                                        mon.LastCursorPosition,
                                        shapeIdCopy
                                    );
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Cursor capture failure should not affect frame capture
                            // Continue (this can happen during desktop transitions)
                        }

                        // Only convert and send if rate limiting allows
                        if (canSendFrame)
                        {
                            if (UseBgraMode)
                            {
                                // BGRA mode - send BGRA texture directly (no color conversion)
                                mon.LastSentTime = loopStart;
                                OnMonitorFrameBgra?.Invoke(mon.Index, mon.LastFrame!, mon.Width, mon.Height, captureTimestamp);
                            }
                            else
                            {
                                // NV12 mode - GPU Video Processor conversion
                                if (mon.ColorConverter == null && mon.Device != null)
                                {
                                    mon.ColorConverter = new GpuColorConverter(mon.Device, mon.Width, mon.Height);
                                    Logger.Info($"[PerMonitorCapture] Monitor {mon.Index}: GpuColorConverter created");
                                }

                                var nv12Texture = mon.ColorConverter?.ConvertToTexture(mon.LastFrame!);
                                mon.LastNV12Frame = nv12Texture;

                                if (nv12Texture != null)
                                {
                                    mon.LastSentTime = loopStart;
                                    OnMonitorFrame?.Invoke(mon.Index, nv12Texture, mon.Width, mon.Height, captureTimestamp);
                                }
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
                    TrySendCachedFrame(mon, canSendFrame, loopStart, captureTimestamp);
                }
                else if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    mon.AccessLostCount++;
                    mon.LastAccessLostTime = DateTime.UtcNow;
                    Logger.Error($"[PerMonitorCapture] Monitor {mon.Index}: ACCESS_LOST (count={mon.AccessLostCount}) - recreating duplication...");

                    TrySendCachedFrame(mon, canSendFrame, loopStart, captureTimestamp);

                    // Wait for Windows to stabilize, then recreate duplication
                    Thread.Sleep(100);
                    if (!RecreateDuplication(mon))
                    {
                        Thread.Sleep(200);
                        RecreateDuplication(mon);
                    }
                }
                else if (!result.Success)
                {
                    Logger.Error($"[PerMonitorCapture] Monitor {mon.Index}: DXGI error 0x{result.Code:X8}");
                    TrySendCachedFrame(mon, canSendFrame, loopStart, captureTimestamp);
                }

                PostEncode:
                // POST-ENCODE SYNC: Wait for all monitors to finish encoding,
                // then the designated thread (mon.Index == 0) sends all buffered
                // frames in alternating order via OnPostEncodeSync.
                if (_postEncodeBarrier != null)
                {
                    try
                    {
                        _postEncodeBarrier.SignalAndWait();
                        // Only one thread flushes — avoids SRTP lock contention
                        // and ensures predictable send timing
                        if (mon.Index == 0)
                        {
                            try { OnPostEncodeSync?.Invoke(); }
                            catch (Exception ex)
                            {
                                Logger.Error($"[PerMonitorCapture] PostEncodeSync error: {ex.Message}");
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }

                // STRICT PACING: Hybrid sleep + spin for CPU-efficient frame timing
                // Sleep threshold is dynamic based on target FPS (set at loop start)
                long remaining = frameTimeMs - (sw.ElapsedMilliseconds - loopStart);
                if (remaining > sleepThresholdMs && sleepThresholdMs > 0)
                {
                    Thread.Sleep((int)(remaining - sleepThresholdMs)); // Yield CPU to OS
                }
                // Fine-grained spin for remaining time (precision timing)
                while ((sw.ElapsedMilliseconds - loopStart) < frameTimeMs)
                {
                    Thread.SpinWait(10);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[PerMonitorCapture] Monitor {mon.Index} error: {ex.Message}");
                Thread.Sleep(50);
            }
        }

        
        Logger.Info($"[PerMonitorCapture] Monitor {mon.Index}: Capture thread stopped");
    }


    /// <summary>
    /// Send the most recent cached frame if rate limiting allows (keeps stream alive during gaps).
    /// </summary>
    private void TrySendCachedFrame(MonitorInfo mon, bool canSendFrame, long loopStart, long captureTimestamp)
    {
        if (!canSendFrame) return;

        if (UseBgraMode && mon.LastFrame != null)
        {
            mon.LastSentTime = loopStart;
            OnMonitorFrameBgra?.Invoke(mon.Index, mon.LastFrame, mon.Width, mon.Height, captureTimestamp);
        }
        else if (mon.LastNV12Frame != null)
        {
            mon.LastSentTime = loopStart;
            OnMonitorFrame?.Invoke(mon.Index, mon.LastNV12Frame, mon.Width, mon.Height, captureTimestamp);
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
            try { mon.Context?.Dispose(); } catch { }
            try { mon.Device?.Dispose(); } catch { }
        }
        Monitors.Clear();
    }
}
