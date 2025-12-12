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
    
    // Cursor capture - default OFF for better performance (avoids software fallback)
    private volatile bool _showCursor = false;
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
    
    // GPU color converter (BGRA -> NV12)
    private GpuColorConverter? _colorConverter;
    private byte[]? _nv12Buffer;
    private bool _useNV12Output;

    private volatile bool _running;
    private Thread? _captureThread;

    /// <summary>CPU frame callback for BGRA (copies to system memory)</summary>
    public event Action<byte[], int, int, int>? OnFrame;
    
    /// <summary>CPU frame callback for NV12 (GPU-converted, then copied to system memory)</summary>
    public event Action<byte[], int, int>? OnNV12Frame;
    
    /// <summary>GPU texture callback for BGRA (zero-copy path, no CPU memory access)</summary>
    public event Action<ID3D11Texture2D, int, int>? OnTextureFrame;
    
    /// <summary>GPU texture callback for NV12 (TRUE ZERO-COPY: GPU-converted NV12 texture)</summary>
    public event Action<ID3D11Texture2D, int, int>? OnNV12TextureFrame;
    
    /// <summary>Expose D3D11 device for encoder initialization</summary>
    public ID3D11Device Device => _device;
    
    /// <summary>Enable NV12 output mode (GPU color conversion)</summary>
    public bool UseNV12Output
    {
        get => _useNV12Output;
        set
        {
            _useNV12Output = value;
            if (value && _colorConverter == null)
            {
                InitializeColorConverter();
            }
        }
    }
    
    private void InitializeColorConverter()
    {
        try
        {
            _colorConverter = new GpuColorConverter(_device, FrameWidth, FrameHeight);
            _nv12Buffer = new byte[FrameWidth * FrameHeight * 3 / 2];
            Console.WriteLine("[ClusterCapture] GPU color converter initialized (BGRA->NV12)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClusterCapture] Failed to initialize GPU color converter: {ex.Message}");
            _colorConverter = null;
            _useNV12Output = false;
        }
    }

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

        // Create D3D11 device with VideoSupport for MFT hardware encoder compatibility
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
        Console.WriteLine($"[ClusterCapture] D3D11 device created (VideoSupport enabled for MFT encoder)");

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

                    // Use very short timeout to avoid blocking on multiple monitors
                    // With 3 monitors at 50ms each = 150ms blocking = only 6 fps
                    // With 5ms x 3 = 15ms, we can achieve 30+ fps
                    var result = dup.AcquireNextFrame(5, out var frameInfo, out var desktopResource);

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

                    // Zero-copy BGRA path: invoke texture callback first
                    OnTextureFrame?.Invoke(_combinedTexture, FrameWidth, FrameHeight);
                    
                    // TRUE ZERO-COPY NV12 path: GPU texture directly to encoder (no CPU copy!)
                    if (_useNV12Output && OnNV12TextureFrame != null && _colorConverter != null && !_showCursor)
                    {
                        try
                        {
                            // Convert BGRA->NV12 on GPU and pass texture directly
                            var nv12Texture = _colorConverter.ConvertToTexture(_combinedTexture);
                            if (nv12Texture != null)
                            {
                                OnNV12TextureFrame(nv12Texture, FrameWidth, FrameHeight);
                            }
                        }
                        catch (ObjectDisposedException) { }
                        catch (NullReferenceException) { }
                    }

                    // NV12 CPU path: GPU conversion then copy to bytes (fallback)
                    if (_useNV12Output && OnNV12Frame != null && _colorConverter != null && _nv12Buffer != null)
                    {
                        try
                        {
                            // Draw cursor on staging texture if enabled
                            if (_showCursor)
                            {
                                // Copy to staging for cursor drawing
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
                                                Buffer.MemoryCopy(src + y * srcStride, dstBase + y * _combinedStride, _combinedStride, _combinedStride);
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    _context.Unmap(_combinedStaging, 0);
                                }
                            
                                // Draw cursor on BGRA buffer
                                DrawCursorOnBuffer(_combinedBuffer, FrameWidth, FrameHeight, _combinedStride);
                            
                                // Convert BGRA with cursor to NV12 via software (since we modified the buffer)
                                ConvertBgraToNv12(_combinedBuffer, _nv12Buffer!, FrameWidth, FrameHeight, _combinedStride);
                                OnNV12Frame(_nv12Buffer, FrameWidth, FrameHeight);
                            }
                            else
                            {
                                // No cursor - use fast GPU conversion
                                var converter = _colorConverter;
                                if (converter != null && converter.Convert(_combinedTexture, _nv12Buffer))
                                {
                                    OnNV12Frame(_nv12Buffer, FrameWidth, FrameHeight);
                                }
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            // Ignore - happens during shutdown
                        }
                        catch (NullReferenceException)
                        {
                            // Ignore - race condition during shutdown
                        }
                    }
                    // BGRA CPU path: only copy to staging if OnFrame has listeners
                    else if (OnFrame != null)
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

    /// <summary>
    /// Software BGRA to NV12 conversion (used when cursor is enabled)
    /// </summary>
    private static void ConvertBgraToNv12(byte[] bgra, byte[] nv12, int width, int height, int bgraStride)
    {
        int yPlaneSize = width * height;
        int uvOffset = yPlaneSize;
        
        // Y plane
        for (int y = 0; y < height; y++)
        {
            int bgraRowOffset = y * bgraStride;
            int yRowOffset = y * width;
            
            for (int x = 0; x < width; x++)
            {
                int bgraIdx = bgraRowOffset + x * 4;
                byte b = bgra[bgraIdx];
                byte g = bgra[bgraIdx + 1];
                byte r = bgra[bgraIdx + 2];
                
                // BT.601 Y conversion
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[yRowOffset + x] = (byte)Math.Clamp(yVal, 0, 255);
            }
        }
        
        // UV plane (interleaved, subsampled 2x2)
        for (int y = 0; y < height; y += 2)
        {
            int uvRowOffset = uvOffset + (y / 2) * width;
            
            for (int x = 0; x < width; x += 2)
            {
                // Average 2x2 block
                int sumR = 0, sumG = 0, sumB = 0;
                for (int dy = 0; dy < 2 && y + dy < height; dy++)
                {
                    for (int dx = 0; dx < 2 && x + dx < width; dx++)
                    {
                        int bgraIdx = (y + dy) * bgraStride + (x + dx) * 4;
                        sumB += bgra[bgraIdx];
                        sumG += bgra[bgraIdx + 1];
                        sumR += bgra[bgraIdx + 2];
                    }
                }
                int r = sumR / 4;
                int g = sumG / 4;
                int b = sumB / 4;
                
                // BT.601 U, V conversion
                int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                
                int uvIdx = uvRowOffset + x;
                nv12[uvIdx] = (byte)Math.Clamp(u, 0, 255);
                nv12[uvIdx + 1] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        try { _colorConverter?.Dispose(); } catch { }
        foreach (var dup in _duplications) try { dup?.Dispose(); } catch { }
        foreach (var stg in _stagings) try { stg?.Dispose(); } catch { }
        try { _combinedTexture?.Dispose(); } catch { }
        try { _combinedStaging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
