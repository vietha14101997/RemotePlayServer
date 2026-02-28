#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Capture;

/// <summary>
/// Screen capture using DXGI Desktop Duplication API.
/// More reliable than WGC for getting D3D11 textures directly.
/// </summary>
public sealed class DxgiCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _staging;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly byte[] _buffer;
    
    private volatile bool _running;
    private Thread? _captureThread;
    private readonly object _lock = new();
    
    public event Action<byte[], int, int, int>? OnFrame;
    public event Action<ID3D11Texture2D, int, int>? OnTextureFrame;
    public (int w, int h) Size => (_width, _height);
    public ID3D11Device Device => _device;
    
    public DxgiCapture(IntPtr hMonitor, int targetFps = 60)
    {
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
                    Logger.Info($"[DXGI] Found output: {desc.DeviceName} on {adapter.Description.Description}");
                    break;
                }
                output.Dispose();
            }
            
            if (targetOutput != null) break;
            adapter.Dispose();
        }
        
        if (targetAdapter == null || targetOutput == null)
        {
            throw new InvalidOperationException($"Could not find DXGI output for monitor handle {hMonitor}");
        }
        
        // Create D3D11 device on the correct adapter with VideoSupport for MFT encoder
        var levels = new[] { FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            targetAdapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels,
            out _device,
            out _context
        );
        Logger.Info($"[DXGI] Created D3D11 device on {targetAdapter.Description.Description} (VideoSupport enabled)");
        targetAdapter.Dispose();
        
        // Get output dimensions
        var outputDesc = targetOutput.Description;
        _width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
        _height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
        _stride = _width * 4;
        _buffer = new byte[_stride * _height];
        
        Logger.Info($"[DXGI] Output size: {_width}x{_height}");
        
        // Create output duplication
        using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
        targetOutput.Dispose();
        
        _duplication = output1.DuplicateOutput(_device);
        Logger.Info("[DXGI] Desktop duplication created");
        
        // Create staging texture for CPU readback
        _staging = _device.CreateTexture2D(new Texture2DDescription
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
            Name = "DXGI-Capture"
        };
        _captureThread.Start();
        Logger.Info("[DXGI] Capture started");
    }
    
    public void Stop()
    {
        _running = false;
        _captureThread?.Join(1000);
        Logger.Info("[DXGI] Capture stopped");
    }
    
    private void CaptureLoop()
    {
        const int frameTimeMs = 16; // ~60fps target
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long frameCount = 0;
        
        while (_running)
        {
            try
            {
                // Try to acquire frame with timeout
                var result = _duplication.AcquireNextFrame(100, out var frameInfo, out var desktopResource);
                
                if (result.Success && desktopResource != null)
                {
                    try
                    {
                        using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
                        
                        frameCount++;
                        if (frameCount == 1 || frameCount % 60 == 0)
                        {
                            Logger.Debug($"[DXGI] Frame #{frameCount}: {_width}x{_height}");
                        }

                        // Invoke texture callback first (zero-copy path)
                        OnTextureFrame?.Invoke(texture, _width, _height);

                        // If CPU callback is registered, copy to staging and read pixels
                        if (OnFrame != null)
                        {
                            _context.CopyResource(_staging, texture);
                            
                            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                            try
                            {
                                unsafe
                                {
                                    byte* src = (byte*)mapped.DataPointer;
                                    for (int y = 0; y < _height; y++)
                                    {
                                        Marshal.Copy((IntPtr)(src + y * mapped.RowPitch), _buffer, y * _stride, _stride);
                                    }
                                }
                            }
                            finally
                            {
                                _context.Unmap(_staging, 0);
                            }
                            
                            OnFrame(_buffer, _width, _height, _stride);
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
                    // No new frame available, continue
                }
                else if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    Logger.Error("[DXGI] Access lost - desktop mode changed?");
                    Thread.Sleep(500);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[DXGI] Capture error: {ex.Message}");
                Thread.Sleep(100);
            }
            
            // Simple frame pacing
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
        try { _duplication?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
