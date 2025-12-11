#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// DXGI Screen capture optimized for GPU pipeline.
/// Exposes both texture and CPU buffer for flexible integration.
/// </summary>
public sealed class DxgiCaptureGpu : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;

    // For CPU readback (compatibility)
    private readonly ID3D11Texture2D _stagingCpu;
    private readonly byte[] _cpuBuffer;

    // For GPU path (zero-copy)
    private ID3D11Texture2D? _currentTexture;

    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _targetFps;

    private volatile bool _running;
    private Thread? _captureThread;
    private readonly object _lock = new();

    // Events for both CPU and GPU consumers
    public event Action<byte[], int, int, int>? OnFrame;
    public event Action<ID3D11Texture2D, int, int>? OnTextureFrame;

    public (int w, int h) Size => (_width, _height);
    public ID3D11Device Device => _device;
    public bool UseGpuPath { get; set; } = false;

    public DxgiCaptureGpu(IntPtr hMonitor, int targetFps = 60)
    {
        _targetFps = targetFps;

        // Find adapter and output for this monitor
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? targetAdapter = null;
        IDXGIOutput? targetOutput = null;

        for (uint ai = 0; ; ai++)
        {
            if (factory.EnumAdapters1(ai, out var adapter).Failure) break;

            for (uint oi = 0; ; oi++)
            {
                if (adapter.EnumOutputs(oi, out var output).Failure) break;

                var desc = output.Description;
                if (desc.Monitor == hMonitor)
                {
                    targetAdapter = adapter;
                    targetOutput = output;
                    Console.WriteLine($"[DXGI-GPU] Found output: {desc.DeviceName} on {adapter.Description.Description}");
                    break;
                }
                output.Dispose();
            }

            if (targetOutput != null) break;
            adapter.Dispose();
        }

        if (targetAdapter == null || targetOutput == null)
            throw new InvalidOperationException($"Could not find DXGI output for monitor handle {hMonitor}");

        // Create D3D11 device with video support
        var levels = new[] { FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            targetAdapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels,
            out _device,
            out _context
        );
        Console.WriteLine($"[DXGI-GPU] Created D3D11 device on {targetAdapter.Description.Description}");
        targetAdapter.Dispose();

        // Get output dimensions
        var outputDesc = targetOutput.Description;
        _width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
        _height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
        _stride = _width * 4;
        _cpuBuffer = new byte[_stride * _height];

        Console.WriteLine($"[DXGI-GPU] Output size: {_width}x{_height}");

        // Create output duplication
        using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
        targetOutput.Dispose();

        _duplication = output1.DuplicateOutput(_device);
        Console.WriteLine("[DXGI-GPU] Desktop duplication created");

        // Create staging texture for CPU readback
        _stagingCpu = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        });
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "DXGI-Capture-GPU"
        };
        _captureThread.Start();
        Console.WriteLine("[DXGI-GPU] Capture started");
    }

    public void Stop()
    {
        _running = false;
        _captureThread?.Join(1000);
        Console.WriteLine("[DXGI-GPU] Capture stopped");
    }

    private void CaptureLoop()
    {
        int frameTimeMs = 1000 / _targetFps;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long frameCount = 0;

        while (_running)
        {
            try
            {
                var result = _duplication.AcquireNextFrame(100, out var frameInfo, out var desktopResource);

                if (result.Success && desktopResource != null)
                {
                    try
                    {
                        using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();

                        frameCount++;
                        if (frameCount == 1 || frameCount % 60 == 0)
                            Console.WriteLine($"[DXGI-GPU] Frame #{frameCount}: {_width}x{_height}");

                        // GPU path: notify texture directly
                        if (UseGpuPath && OnTextureFrame != null)
                        {
                            OnTextureFrame(texture, _width, _height);
                        }

                        // CPU path: copy to staging and read pixels
                        if (OnFrame != null)
                        {
                            _context.CopyResource(_stagingCpu, texture);
                            var mapped = _context.Map(_stagingCpu, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                            try
                            {
                                unsafe
                                {
                                    byte* src = (byte*)mapped.DataPointer;
                                    // Fast copy if strides match
                                    if (mapped.RowPitch == _stride)
                                    {
                                        Marshal.Copy(mapped.DataPointer, _cpuBuffer, 0, _cpuBuffer.Length);
                                    }
                                    else
                                    {
                                        for (int y = 0; y < _height; y++)
                                        {
                                            Marshal.Copy((IntPtr)(src + y * mapped.RowPitch), _cpuBuffer, y * _stride, _stride);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                _context.Unmap(_stagingCpu, 0);
                            }

                            OnFrame(_cpuBuffer, _width, _height, _stride);
                        }
                    }
                    finally
                    {
                        desktopResource.Dispose();
                        _duplication.ReleaseFrame();
                    }
                }
                else if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    // No new frame
                }
                else if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    Console.WriteLine("[DXGI-GPU] Access lost - desktop mode changed?");
                    Thread.Sleep(500);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI-GPU] Capture error: {ex.Message}");
                Thread.Sleep(100);
            }

            // Frame pacing
            var elapsed = sw.ElapsedMilliseconds;
            var sleepTime = frameTimeMs - (int)(elapsed % frameTimeMs);
            if (sleepTime > 0 && sleepTime < frameTimeMs)
                Thread.Sleep(sleepTime);
        }
    }

    public void Dispose()
    {
        Stop();
        try { _duplication?.Dispose(); } catch { }
        try { _stagingCpu?.Dispose(); } catch { }
        try { _currentTexture?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
