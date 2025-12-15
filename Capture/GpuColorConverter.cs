#nullable enable
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// GPU-accelerated BGRA to NV12 color conversion using D3D11 Video Processor.
/// This eliminates CPU-bound color conversion bottleneck for NVENC encoding.
/// Note: Video Processor is disabled for NVIDIA GPUs due to compatibility issues
/// with Desktop Duplication textures (causes E_INVALIDARG and device lost).
/// </summary>
public sealed class GpuColorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessor? _videoProcessor;
    private readonly ID3D11VideoProcessorEnumerator? _vpEnum;
    
    private ID3D11Texture2D? _nv12Texture;
    private ID3D11Texture2D? _nv12Staging;
    private ID3D11VideoProcessorOutputView? _outputView;
    
    private int _width;
    private int _height;
    private readonly byte[] _nv12Buffer;
    private bool _disposed;
    
    // Video Processor is disabled for NVIDIA due to Desktop Duplication texture incompatibility
    private readonly bool _useVideoProcessor;

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
        
        // Detect GPU vendor
        // Note: Previously disabled Video Processor for NVIDIA due to Desktop Duplication issues,
        // but CPU fallback causes DEVICE_REMOVED when D3D11VA encoder is active.
        // Now enabling Video Processor for all GPUs - NVIDIA D3D11VA handles the conversion better.
        var vendor = GpuVendorDetector.DetectPrimaryGpuVendor();
        _useVideoProcessor = true; // Always use GPU Video Processor
        
        Console.WriteLine($"[GpuColorConverter] GPU: {vendor}, using Video Processor for BGRA->NV12 conversion");
        
        // Query video device interface
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        
        // Create Video Processor for all GPUs (BGRA->NV12 conversion)
        if (_useVideoProcessor)
        {
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
        }
        
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
        
        // NV12 staging texture for CPU readback/write (CPU path needs Write for NVIDIA)
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
            CPUAccessFlags = CpuAccessFlags.Read | CpuAccessFlags.Write
        });
        
        // Create output view for NV12 texture (only if using Video Processor)
        if (_useVideoProcessor && _vpEnum != null)
        {
            var outputViewDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            };
            outputViewDesc.Texture2D.MipSlice = 0;
            _outputView = _videoDevice.CreateVideoProcessorOutputView(_nv12Texture, _vpEnum, outputViewDesc);
        }
        
        _width = width;
        _height = height;
    }
    
    /// <summary>
    /// Convert BGRA texture to NV12 on GPU and copy to CPU buffer.
    /// </summary>
    public bool Convert(ID3D11Texture2D bgraTexture, byte[] outputNV12Buffer)
    {
        if (_disposed) return false;
        
        // For NVIDIA, use CPU conversion to avoid Video Processor issues
        if (!_useVideoProcessor || _vpEnum == null || _videoProcessor == null)
        {
            return ConvertViaCPU(bgraTexture, outputNV12Buffer);
        }
        
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
            
            using var inputView = _videoDevice.CreateVideoProcessorInputView(bgraTexture, _vpEnum!, inputViewDesc);
            
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
    /// CPU fallback for Convert() - used when Video Processor is unavailable (NVIDIA)
    /// </summary>
    private bool ConvertViaCPU(ID3D11Texture2D bgraTexture, byte[] outputNV12Buffer)
    {
        try
        {
            // Create staging texture for CPU read if not exists
            if (_bgraStagingTexture == null)
            {
                _bgraStagingTexture = _device.CreateTexture2D(new Texture2DDescription
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
            
            // Copy BGRA texture to staging
            _context.CopyResource(_bgraStagingTexture, bgraTexture);
            
            // Map and convert
            var mapped = _context.Map(_bgraStagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    int srcPitch = (int)mapped.RowPitch;
                    
                    fixed (byte* dst = outputNV12Buffer)
                    {
                        ConvertBgraToNv12(src, srcPitch, dst, _width, _width, _height);
                    }
                }
            }
            finally
            {
                _context.Unmap(_bgraStagingTexture, 0);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuColorConverter] ConvertViaCPU error: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Convert BGRA texture to NV12 on GPU only (zero-copy path).
    /// Returns the NV12 texture directly without CPU readback.
    /// Uses CPU conversion for NVIDIA GPUs (Video Processor incompatible with Desktop Duplication).
    /// </summary>
    public ID3D11Texture2D? ConvertToTexture(ID3D11Texture2D bgraTexture)
    {
        if (_disposed) return null;
        if (bgraTexture == null)
        {
            Console.WriteLine("[GpuColorConverter] ConvertToTexture: input texture is null");
            return null;
        }
        
        // Use GPU Video Processor for AMD/Intel, CPU fallback for NVIDIA
        if (_useVideoProcessor && _videoProcessor != null && _vpEnum != null)
        {
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
                
                using var inputView = _videoDevice.CreateVideoProcessorInputView(bgraTexture, _vpEnum!, inputViewDesc);
                
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
                Console.WriteLine($"[GpuColorConverter] Video Processor error: {ex.Message}");
                // Don't fall through to CPU - return null to avoid device corruption
                return null;
            }
        }
        
        // CPU path for NVIDIA (Video Processor causes device lost with Desktop Duplication textures)
        return ConvertToTextureViaCPU(bgraTexture);
    }
    
    private ID3D11Texture2D? _bgraStagingTexture;
    
    /// <summary>
    /// CPU fallback for BGRA->NV12 conversion when Video Processor is not available
    /// </summary>
    private ID3D11Texture2D? ConvertToTextureViaCPU(ID3D11Texture2D bgraTexture)
    {
        try
        {
            // Create staging texture for CPU read if not exists
            if (_bgraStagingTexture == null)
            {
                _bgraStagingTexture = _device.CreateTexture2D(new Texture2DDescription
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
            
            // Copy BGRA texture to staging
            _context.CopyResource(_bgraStagingTexture, bgraTexture);
            
            // Map BGRA staging texture
            var bgraMapped = _context.Map(_bgraStagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            
            try
            {
                // Map NV12 staging texture for write
                var nv12Mapped = _context.Map(_nv12Staging!, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                
                try
                {
                    unsafe
                    {
                        byte* bgraSrc = (byte*)bgraMapped.DataPointer;
                        int bgraPitch = (int)bgraMapped.RowPitch;
                        byte* nv12Dst = (byte*)nv12Mapped.DataPointer;
                        int nv12Pitch = (int)nv12Mapped.RowPitch;
                        
                        // Convert BGRA to NV12 (Y plane + UV plane)
                        ConvertBgraToNv12(bgraSrc, bgraPitch, nv12Dst, nv12Pitch, _width, _height);
                    }
                }
                finally
                {
                    _context.Unmap(_nv12Staging!, 0);
                }
            }
            finally
            {
                _context.Unmap(_bgraStagingTexture, 0);
            }
            
            // Copy from staging to output texture
            _context.CopyResource(_nv12Texture!, _nv12Staging!);
            
            return _nv12Texture;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuColorConverter] CPU fallback error: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Convert BGRA to NV12 in CPU memory
    /// </summary>
    private static unsafe void ConvertBgraToNv12(byte* bgra, int bgraPitch, byte* nv12, int nv12Pitch, int width, int height)
    {
        // Y plane
        byte* yPlane = nv12;
        for (int y = 0; y < height; y++)
        {
            byte* bgraRow = bgra + y * bgraPitch;
            byte* yRow = yPlane + y * nv12Pitch;
            
            for (int x = 0; x < width; x++)
            {
                byte b = bgraRow[x * 4 + 0];
                byte g = bgraRow[x * 4 + 1];
                byte r = bgraRow[x * 4 + 2];
                
                // BT.601 Y conversion
                yRow[x] = (byte)Math.Clamp((66 * r + 129 * g + 25 * b + 128 >> 8) + 16, 0, 255);
            }
        }
        
        // UV plane (interleaved, half resolution)
        byte* uvPlane = nv12 + nv12Pitch * height;
        for (int y = 0; y < height / 2; y++)
        {
            byte* bgraRow0 = bgra + (y * 2) * bgraPitch;
            byte* bgraRow1 = bgra + (y * 2 + 1) * bgraPitch;
            byte* uvRow = uvPlane + y * nv12Pitch;
            
            for (int x = 0; x < width / 2; x++)
            {
                // Average 2x2 block
                int b = (bgraRow0[x * 8 + 0] + bgraRow0[x * 8 + 4] + bgraRow1[x * 8 + 0] + bgraRow1[x * 8 + 4]) / 4;
                int g = (bgraRow0[x * 8 + 1] + bgraRow0[x * 8 + 5] + bgraRow1[x * 8 + 1] + bgraRow1[x * 8 + 5]) / 4;
                int r = (bgraRow0[x * 8 + 2] + bgraRow0[x * 8 + 6] + bgraRow1[x * 8 + 2] + bgraRow1[x * 8 + 6]) / 4;
                
                // BT.601 U, V conversion
                uvRow[x * 2 + 0] = (byte)Math.Clamp((-38 * r - 74 * g + 112 * b + 128 >> 8) + 128, 0, 255); // U
                uvRow[x * 2 + 1] = (byte)Math.Clamp((112 * r - 94 * g - 18 * b + 128 >> 8) + 128, 0, 255);  // V
            }
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
