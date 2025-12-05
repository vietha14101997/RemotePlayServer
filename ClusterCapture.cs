#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Captures multiple monitors and combines them into a single side-by-side frame.
/// Uses single NVENC encoder for optimal performance.
/// Layout: [Monitor0 | Monitor1 | Monitor2] horizontally
/// </summary>
public sealed class ClusterCapture : IDisposable
{
    public readonly int CellWidth;
    public readonly int CellHeight;
    public readonly int Gap;
    public readonly int FrameWidth;
    public readonly int FrameHeight;
    public readonly int MonitorCount;
    public readonly int TargetFps;

    // NVENC max width = 4096
    private const int NVENC_MAX_WIDTH = 4096;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly List<IDXGIOutputDuplication> _duplications = new();
    private readonly List<ID3D11Texture2D> _stagings = new();
    private readonly List<(int x, int y, int w, int h)> _monitorLayouts = new();
    
    private readonly ID3D11Texture2D _combinedStaging;
    private readonly byte[] _combinedBuffer;
    private readonly int _combinedStride;

    private volatile bool _running;
    private Thread? _captureThread;

    public event Action<byte[], int, int, int>? OnFrame;

    public ClusterCapture(List<(IntPtr hmon, string name, int w, int h)> monitors, int gap = 1, int targetFps = 30)
    {
        if (monitors == null || monitors.Count == 0)
            throw new ArgumentException("At least one monitor required");

        MonitorCount = monitors.Count;
        Gap = gap;
        TargetFps = Math.Clamp(targetFps, 10, 60);

        // Calculate combined frame dimensions
        // All monitors side-by-side horizontally
        int maxHeight = 0;
        int totalWidth = 0;
        foreach (var mon in monitors)
        {
            if (mon.h > maxHeight) maxHeight = mon.h;
            totalWidth += mon.w;
        }
        totalWidth += (monitors.Count - 1) * gap; // gaps between monitors
        
        // Check NVENC limit - use CROP instead of scale for better performance
        if (totalWidth > NVENC_MAX_WIDTH)
        {
            // Calculate how much we need to trim from each monitor
            int excess = totalWidth - NVENC_MAX_WIDTH;
            int trimPerMonitor = (excess + monitors.Count - 1) / monitors.Count; // round up
            
            CellWidth = monitors[0].w - trimPerMonitor;
            CellHeight = maxHeight;
            FrameWidth = CellWidth * monitors.Count + (monitors.Count - 1) * gap;
            FrameHeight = maxHeight;
            
            // Ensure we don't exceed limit
            if (FrameWidth > NVENC_MAX_WIDTH)
            {
                FrameWidth = NVENC_MAX_WIDTH;
                CellWidth = (NVENC_MAX_WIDTH - (monitors.Count - 1) * gap) / monitors.Count;
            }
            
            Console.WriteLine($"[ClusterCapture] Cropping: {totalWidth}x{maxHeight} -> {FrameWidth}x{FrameHeight} (trimPerMon={trimPerMonitor})");
        }
        else
        {
            FrameWidth = totalWidth;
            FrameHeight = maxHeight;
            CellWidth = monitors[0].w;
            CellHeight = monitors[0].h;
        }
        
        Gap = gap;
        _combinedStride = FrameWidth * 4;
        _combinedBuffer = new byte[_combinedStride * FrameHeight];

        Console.WriteLine($"[ClusterCapture] Combined frame: {FrameWidth}x{FrameHeight}, {MonitorCount} monitors, gap={Gap}");

        // Find the primary adapter (use first monitor's adapter)
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        
        IDXGIAdapter1? primaryAdapter = null;
        for (uint ai = 0; ; ai++)
        {
            if (factory.EnumAdapters1(ai, out var adapter).Failure) break;
            
            for (uint oi = 0; ; oi++)
            {
                if (adapter.EnumOutputs(oi, out var output).Failure) break;
                
                var desc = output.Description;
                if (desc.Monitor == monitors[0].hmon)
                {
                    primaryAdapter = adapter;
                    Console.WriteLine($"[ClusterCapture] Using adapter: {adapter.Description.Description}");
                    output.Dispose();
                    break;
                }
                output.Dispose();
            }
            
            if (primaryAdapter != null) break;
            adapter.Dispose();
        }

        if (primaryAdapter == null)
            throw new InvalidOperationException("Could not find adapter for monitors");

        // Create D3D11 device
        var levels = new[] { FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            primaryAdapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            levels,
            out _device,
            out _context
        );
        primaryAdapter.Dispose();

        // Setup duplication for each monitor
        int xOffset = 0;
        foreach (var mon in monitors)
        {
            _monitorLayouts.Add((xOffset, 0, mon.w, mon.h));
            xOffset += mon.w + gap;

            // Find and create duplication for this monitor
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

            if (targetOutput == null)
            {
                Console.WriteLine($"[ClusterCapture] Warning: Could not find output for {mon.name}");
                continue;
            }

            using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
            targetOutput.Dispose();

            var dup = output1.DuplicateOutput(_device);
            _duplications.Add(dup);

            // Create staging texture for this monitor
            var staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)mon.w,
                Height = (uint)mon.h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            });
            _stagings.Add(staging);

            Console.WriteLine($"[ClusterCapture] Added monitor: {mon.name} at offset x={_monitorLayouts[^1].x}");
        }

        // Create combined staging texture
        _combinedStaging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)FrameWidth,
            Height = (uint)FrameHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        Console.WriteLine($"[ClusterCapture] Initialized with {_duplications.Count} monitor duplications");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "Cluster-Capture"
        };
        _captureThread.Start();
        Console.WriteLine("[ClusterCapture] Capture started");
    }

    public void Stop()
    {
        _running = false;
        _captureThread?.Join(1000);
        Console.WriteLine("[ClusterCapture] Capture stopped");
    }

    private void CaptureLoop()
    {
        int frameTimeMs = 1000 / TargetFps; // e.g., 33ms for 30fps
        Console.WriteLine($"[ClusterCapture] Target {TargetFps}fps, frame interval {frameTimeMs}ms");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long frameCount = 0;

        // Clear buffer once at start (black background for gaps)
        Array.Clear(_combinedBuffer, 0, _combinedBuffer.Length);

        while (_running)
        {
            try
            {
                // DON'T clear buffer every frame - keep previous frame data to avoid flickering
                bool anyFrameCaptured = false;
                int dstXOffset = 0;

                // Capture each monitor and copy directly to combined buffer (with crop if needed)
                for (int i = 0; i < _duplications.Count; i++)
                {
                    var dup = _duplications[i];
                    var staging = _stagings[i];
                    var layout = _monitorLayouts[i];

                    var result = dup.AcquireNextFrame(50, out var frameInfo, out var desktopResource);

                    if (result.Success && desktopResource != null)
                    {
                        try
                        {
                            using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
                            _context.CopyResource(staging, texture);

                            var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                            try
                            {
                                int srcStride = (int)mapped.RowPitch;
                                int copyWidth = Math.Min(CellWidth, layout.w);
                                int copyHeight = Math.Min(FrameHeight, layout.h);
                                int copyBytes = copyWidth * 4;

                                unsafe
                                {
                                    byte* src = (byte*)mapped.DataPointer;
                                    
                                    fixed (byte* dstBase = _combinedBuffer)
                                    {
                                        byte* dst = dstBase + dstXOffset * 4;
                                        
                                        // Fast bulk copy using Buffer.MemoryCopy
                                        for (int y = 0; y < copyHeight; y++)
                                        {
                                            Buffer.MemoryCopy(
                                                src + y * srcStride,
                                                dst + y * _combinedStride,
                                                copyBytes,
                                                copyBytes
                                            );
                                        }
                                    }
                                }

                                anyFrameCaptured = true;
                            }
                            finally
                            {
                                _context.Unmap(staging, 0);
                            }
                        }
                        finally
                        {
                            desktopResource.Dispose();
                            dup.ReleaseFrame();
                        }
                    }
                    else if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                    {
                        // No new frame, keep previous content
                        anyFrameCaptured = true;
                    }

                    // Move to next cell position
                    dstXOffset += CellWidth + Gap;
                }

                if (anyFrameCaptured)
                {
                    frameCount++;
                    if (frameCount == 1 || frameCount % 60 == 0)
                    {
                        Console.WriteLine($"[ClusterCapture] Frame #{frameCount}: {FrameWidth}x{FrameHeight}");
                    }

                    OnFrame?.Invoke(_combinedBuffer, FrameWidth, FrameHeight, _combinedStride);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClusterCapture] Error: {ex.Message}");
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
        foreach (var dup in _duplications) try { dup?.Dispose(); } catch { }
        foreach (var stg in _stagings) try { stg?.Dispose(); } catch { }
        try { _combinedStaging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
