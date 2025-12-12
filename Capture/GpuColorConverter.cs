#nullable enable
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// GPU-accelerated BGRA to NV12 color conversion using D3D11 Video Processor.
/// This eliminates CPU-bound color conversion bottleneck for NVENC encoding.
/// </summary>
public sealed class GpuColorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessor _videoProcessor;
    private readonly ID3D11VideoProcessorEnumerator _vpEnum;
    
    private ID3D11Texture2D? _nv12Texture;
    private ID3D11Texture2D? _nv12Staging;
    private ID3D11VideoProcessorOutputView? _outputView;
    
    private int _width;
    private int _height;
    private readonly byte[] _nv12Buffer;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;
    public int NV12Size => _width * _height * 3 / 2;
    
    /// <summary>
    /// GPU NV12 texture for zero-copy encoding path
    /// </summary>
    public ID3D11Texture2D? NV12Texture => _nv12Texture;

    public GpuColorConverter(ID3D11Device device, int width, int height)
    {
        _device = device;
        _width = width;
        _height = height;
        _nv12Buffer = new byte[NV12Size];
        
        _context = device.ImmediateContext;
        
        // Query video device interface
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        
        // Create video processor enumerator
        var vpContentDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal
        };
        
        _vpEnum = _videoDevice.CreateVideoProcessorEnumerator(vpContentDesc);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_vpEnum, 0);
        
        // Create NV12 output texture
        CreateNV12Textures(width, height);
        
        Console.WriteLine($"[GpuColorConverter] Initialized {width}x{height} BGRA->NV12 converter");
    }
    
    private void CreateNV12Textures(int width, int height)
    {
        // Dispose old textures if resizing
        _outputView?.Dispose();
        _nv12Texture?.Dispose();
        _nv12Staging?.Dispose();
        
        // NV12 output texture (GPU)
        _nv12Texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None
        });
        
        // NV12 staging texture for CPU readback
        _nv12Staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        });
        
        // Create output view for NV12 texture
        var outputViewDesc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D
        };
        outputViewDesc.Texture2D.MipSlice = 0;
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_nv12Texture, _vpEnum, outputViewDesc);
        
        _width = width;
        _height = height;
    }
    
    /// <summary>
    /// Convert BGRA texture to NV12 on GPU and copy to CPU buffer.
    /// </summary>
    public bool Convert(ID3D11Texture2D bgraTexture, byte[] outputNV12Buffer)
    {
        if (_disposed) return false;
        
        try
        {
            // Create input view for BGRA texture (needs to be recreated each time as texture may change)
            var inputViewDesc = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D
            };
            inputViewDesc.Texture2D.MipSlice = 0;
            inputViewDesc.Texture2D.ArraySlice = 0;
            
            using var inputView = _videoDevice.CreateVideoProcessorInputView(bgraTexture, _vpEnum, inputViewDesc);
            
            // Setup video processor stream
            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = inputView,
                InputSurfaceRight = null
            };
            
            // Perform color conversion on GPU
            _videoContext.VideoProcessorBlt(_videoProcessor, _outputView!, 0, 1, new[] { stream });
            
            // Copy NV12 texture to staging for CPU readback
            _context.CopyResource(_nv12Staging!, _nv12Texture!);
            
            // Map and copy NV12 data
            var mapped = _context.Map(_nv12Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyNV12Data(mapped, outputNV12Buffer);
            }
            finally
            {
                _context.Unmap(_nv12Staging!, 0);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuColorConverter] Convert error: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Convert BGRA texture to NV12 and return the internal buffer.
    /// </summary>
    public byte[]? Convert(ID3D11Texture2D bgraTexture)
    {
        if (Convert(bgraTexture, _nv12Buffer))
            return _nv12Buffer;
        return null;
    }
    
    /// <summary>
    /// Convert BGRA texture to NV12 on GPU only (zero-copy path).
    /// Returns the NV12 texture directly without CPU readback.
    /// </summary>
    public ID3D11Texture2D? ConvertToTexture(ID3D11Texture2D bgraTexture)
    {
        if (_disposed) return null;
        
        try
        {
            // Create input view for BGRA texture
            var inputViewDesc = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D
            };
            inputViewDesc.Texture2D.MipSlice = 0;
            inputViewDesc.Texture2D.ArraySlice = 0;
            
            using var inputView = _videoDevice.CreateVideoProcessorInputView(bgraTexture, _vpEnum, inputViewDesc);
            
            // Setup video processor stream
            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = inputView,
                InputSurfaceRight = null
            };
            
            // Perform color conversion on GPU (no CPU readback)
            _videoContext.VideoProcessorBlt(_videoProcessor, _outputView!, 0, 1, new[] { stream });
            
            return _nv12Texture;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuColorConverter] ConvertToTexture error: {ex.Message}");
            return null;
        }
    }
    
    private void CopyNV12Data(MappedSubresource mapped, byte[] outputBuffer)
    {
        unsafe
        {
            byte* src = (byte*)mapped.DataPointer;
            int srcRowPitch = (int)mapped.RowPitch;
            
            // NV12 format: Y plane (full resolution) followed by interleaved UV plane (half resolution)
            int yPlaneSize = _width * _height;
            int uvPlaneSize = _width * _height / 2;
            
            fixed (byte* dst = outputBuffer)
            {
                // Copy Y plane
                if (srcRowPitch == _width)
                {
                    // Contiguous - single copy
                    Buffer.MemoryCopy(src, dst, yPlaneSize, yPlaneSize);
                }
                else
                {
                    // Row by row copy for Y plane
                    for (int y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(
                            src + y * srcRowPitch,
                            dst + y * _width,
                            _width,
                            _width
                        );
                    }
                }
                
                // UV plane starts after Y plane in the texture (at height offset in NV12)
                byte* uvSrc = src + srcRowPitch * _height;
                byte* uvDst = dst + yPlaneSize;
                
                int uvHeight = _height / 2;
                if (srcRowPitch == _width)
                {
                    Buffer.MemoryCopy(uvSrc, uvDst, uvPlaneSize, uvPlaneSize);
                }
                else
                {
                    for (int y = 0; y < uvHeight; y++)
                    {
                        Buffer.MemoryCopy(
                            uvSrc + y * srcRowPitch,
                            uvDst + y * _width,
                            _width,
                            _width
                        );
                    }
                }
            }
        }
    }
    
    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth == _width && newHeight == _height) return;
        CreateNV12Textures(newWidth, newHeight);
        Console.WriteLine($"[GpuColorConverter] Resized to {newWidth}x{newHeight}");
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _outputView?.Dispose();
        _nv12Texture?.Dispose();
        _nv12Staging?.Dispose();
        _videoProcessor?.Dispose();
        _vpEnum?.Dispose();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
    }
}
