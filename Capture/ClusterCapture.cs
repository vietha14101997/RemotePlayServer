#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

/// <summary>
/// Captures multiple monitors and combines them into a single side-by-side frame.
/// Uses single NVENC encoder for optimal performance.
/// Layout: [Monitor0 | Monitor1 | Monitor2] horizontally
/// Supports zero-copy GPU path via OnTextureFrame event.
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
    
    // Cursor capture
    private volatile bool _showCursor = true;
    public bool ShowCursor { get => _showCursor; set => _showCursor = value; }
    
    // Cursor P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
    
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, uint frame, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }
    
    private const uint DI_NORMAL = 0x0003;
    private const int CURSOR_SHOWING = 0x00000001;
    
    // GPU combined texture for zero-copy path
    private readonly ID3D11Texture2D _combinedTexture;
    private readonly ID3D11Texture2D _combinedStaging;
    private readonly byte[] _combinedBuffer;
    private readonly int _combinedStride;

    private volatile bool _running;
    private Thread? _captureThread;

    /// <summary>CPU frame callback (copies to system memory)</summary>
    public event Action<byte[], int, int, int>? OnFrame;
    
    /// <summary>GPU texture callback (zero-copy path, no CPU memory access)</summary>
    public event Action<ID3D11Texture2D, int, int>? OnTextureFrame;
    
    /// <summary>Expose D3D11 device for encoder initialization</summary>
    public ID3D11Device Device => _device;

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
        
        // H.264 and Video Processor require even dimensions (multiple of 2)
        // Align down to even numbers
        if (FrameWidth % 2 != 0)
        {
            FrameWidth = FrameWidth - 1;
            Console.WriteLine($"[ClusterCapture] Aligned FrameWidth to even: {FrameWidth}");
        }
        if (FrameHeight % 2 != 0)
        {
            FrameHeight = FrameHeight - 1;
            Console.WriteLine($"[ClusterCapture] Aligned FrameHeight to even: {FrameHeight}");
        }
        if (CellWidth % 2 != 0)
        {
            CellWidth = CellWidth - 1;
        }
        if (CellHeight % 2 != 0)
        {
            CellHeight = CellHeight - 1;
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

        // Create GPU combined texture for zero-copy compositing
        // Video processor input view requires RenderTarget bind flag
        _combinedTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)FrameWidth,
            Height = (uint)FrameHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None
        });

        // Create combined staging texture for CPU readback (only used when OnFrame has listeners)
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
            CPUAccessFlags = CpuAccessFlags.Read
        });

        Console.WriteLine($"[ClusterCapture] Initialized with {_duplications.Count} monitor duplications (zero-copy enabled)");
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
                bool anyFrameCaptured = false;
                int dstXOffset = 0;

                // Capture each monitor and composite directly on GPU
                for (int i = 0; i < _duplications.Count; i++)
                {
                    var dup = _duplications[i];
                    var layout = _monitorLayouts[i];

                    var result = dup.AcquireNextFrame(50, out var frameInfo, out var desktopResource);

                    if (result.Success && desktopResource != null)
                    {
                        try
                        {
                            using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
                            
                            // GPU compositing: copy region from source texture to combined texture
                            int copyWidth = Math.Min(CellWidth, layout.w);
                            int copyHeight = Math.Min(FrameHeight, layout.h);
                            
                            // CopySubresourceRegion: copy from source texture to destination region
                            var srcBox = new Box(0, 0, 0, copyWidth, copyHeight, 1);
                            _context.CopySubresourceRegion(
                                _combinedTexture, 0,      // dst texture, subresource
                                (uint)dstXOffset, 0, 0,   // dst x, y, z
                                texture, 0,               // src texture, subresource
                                srcBox                    // src region
                            );

                            anyFrameCaptured = true;
                        }
                        finally
                        {
                            desktopResource.Dispose();
                            dup.ReleaseFrame();
                        }
                    }
                    else if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                    {
                        // No new frame, keep previous content in combined texture
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
                        Console.WriteLine($"[ClusterCapture] Frame #{frameCount}: {FrameWidth}x{FrameHeight} (GPU composited)");
                    }

                    // Zero-copy path: invoke texture callback first
                    OnTextureFrame?.Invoke(_combinedTexture, FrameWidth, FrameHeight);

                    // CPU path: only copy to staging if OnFrame has listeners
                    if (OnFrame != null)
                    {
                        // Copy combined GPU texture to staging for CPU readback
                        _context.CopyResource(_combinedStaging, _combinedTexture);
                        
                        var mapped = _context.Map(_combinedStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            unsafe
                            {
                                byte* src = (byte*)mapped.DataPointer;
                                int srcStride = (int)mapped.RowPitch;
                                
                                fixed (byte* dstBase = _combinedBuffer)
                                {
                                    for (int y = 0; y < FrameHeight; y++)
                                    {
                                        Buffer.MemoryCopy(
                                            src + y * srcStride,
                                            dstBase + y * _combinedStride,
                                            _combinedStride,
                                            _combinedStride
                                        );
                                    }
                                }
                            }
                        }
                        finally
                        {
                            _context.Unmap(_combinedStaging, 0);
                        }

                        // Draw cursor if enabled
                        if (_showCursor)
                        {
                            DrawCursorOnBuffer(_combinedBuffer, FrameWidth, FrameHeight, _combinedStride);
                        }

                        OnFrame(_combinedBuffer, FrameWidth, FrameHeight, _combinedStride);
                    }
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

    private void DrawCursorOnBuffer(byte[] buffer, int width, int height, int stride)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
                return;

            // Get cursor hotspot
            if (!GetIconInfo(ci.hCursor, out var iconInfo))
                return;
            
            try
            {
                // Calculate cursor position in combined frame coordinates
                // Screen position -> combined frame position
                int cursorX = ci.ptScreenPos.X - iconInfo.xHotspot;
                int cursorY = ci.ptScreenPos.Y - iconInfo.yHotspot;
                
                // Map screen coordinates to our combined frame
                // Find which monitor the cursor is on
                int frameX = -1, frameY = -1;
                int accX = 0;
                for (int i = 0; i < _monitorLayouts.Count; i++)
                {
                    var layout = _monitorLayouts[i];
                    if (cursorX >= layout.x && cursorX < layout.x + layout.w &&
                        cursorY >= layout.y && cursorY < layout.y + layout.h)
                    {
                        // Cursor is on this monitor
                        frameX = accX + (cursorX - layout.x);
                        frameY = cursorY - layout.y;
                        
                        // Clamp to cell bounds
                        if (frameX >= accX + CellWidth) frameX = accX + CellWidth - 1;
                        break;
                    }
                    accX += CellWidth + Gap;
                }
                
                if (frameX < 0 || frameY < 0) return;
                if (frameX >= width || frameY >= height) return;

                // Draw cursor using GDI
                const int cursorSize = 32;
                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = cursorSize,
                        biHeight = -cursorSize, // top-down
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0
                    }
                };

                IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
                IntPtr dib = CreateDIBSection(hdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
                IntPtr oldBmp = SelectObject(hdc, dib);

                // Draw cursor to DIB
                DrawIconEx(hdc, 0, 0, ci.hCursor, cursorSize, cursorSize, 0, IntPtr.Zero, DI_NORMAL);

                // Copy cursor pixels to buffer with alpha blending
                if (bits != IntPtr.Zero)
                {
                    unsafe
                    {
                        byte* cursorData = (byte*)bits;
                        fixed (byte* bufPtr = buffer)
                        {
                            for (int cy = 0; cy < cursorSize; cy++)
                            {
                                int destY = frameY + cy;
                                if (destY < 0 || destY >= height) continue;
                                
                                for (int cx = 0; cx < cursorSize; cx++)
                                {
                                    int destX = frameX + cx;
                                    if (destX < 0 || destX >= width) continue;
                                    
                                    int srcIdx = (cy * cursorSize + cx) * 4;
                                    int dstIdx = destY * stride + destX * 4;
                                    
                                    byte a = cursorData[srcIdx + 3];
                                    if (a == 0) continue;
                                    
                                    if (a == 255)
                                    {
                                        bufPtr[dstIdx + 0] = cursorData[srcIdx + 0]; // B
                                        bufPtr[dstIdx + 1] = cursorData[srcIdx + 1]; // G
                                        bufPtr[dstIdx + 2] = cursorData[srcIdx + 2]; // R
                                        bufPtr[dstIdx + 3] = 255;
                                    }
                                    else
                                    {
                                        // Alpha blend
                                        int invA = 255 - a;
                                        bufPtr[dstIdx + 0] = (byte)((cursorData[srcIdx + 0] * a + bufPtr[dstIdx + 0] * invA) / 255);
                                        bufPtr[dstIdx + 1] = (byte)((cursorData[srcIdx + 1] * a + bufPtr[dstIdx + 1] * invA) / 255);
                                        bufPtr[dstIdx + 2] = (byte)((cursorData[srcIdx + 2] * a + bufPtr[dstIdx + 2] * invA) / 255);
                                    }
                                }
                            }
                        }
                    }
                }

                SelectObject(hdc, oldBmp);
                DeleteObject(dib);
                DeleteDC(hdc);
            }
            finally
            {
                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            }
        }
        catch { /* Ignore cursor drawing errors */ }
    }

    public void Dispose()
    {
        Stop();
        foreach (var dup in _duplications) try { dup?.Dispose(); } catch { }
        foreach (var stg in _stagings) try { stg?.Dispose(); } catch { }
        try { _combinedTexture?.Dispose(); } catch { }
        try { _combinedStaging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
