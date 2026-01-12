#nullable enable
using System;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemotePlayServer.Encoding;

/// <summary>
/// Adapter for LibAvEncoder to implement ITextureEncoder interface
/// Used for NVIDIA (NVENC) and Intel (QSV) GPUs
/// Handles texture to NV12 bytes conversion for LibAvEncoder
/// Supports both H.264 and H.265 codecs with automatic fallback
/// </summary>
public class LibAvEncoderAdapter : ITextureEncoder
{
    private LibAvEncoder? _encoder;
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _stagingTexture;
    private byte[]? _nv12Buffer;
    private bool _disposed;
    private long _frameCount;
    private VideoCodec _preferredCodec = VideoCodec.H265;

    public event Action<byte[], bool, long>? OnEncodedData;

    public bool IsInitialized => _encoder?.IsInitialized ?? false;
    public int Width => _width;
    public int Height => _height;
    public int CurrentBitrateKbps => _encoder?.CurrentBitrateKbps ?? _bitrate;
    public int CurrentFps => _fps;

    /// <summary>
    /// The currently active video codec (H264 or H265)
    /// </summary>
    public VideoCodec CurrentCodec => _encoder?.CurrentCodec ?? _preferredCodec;

    /// <summary>
    /// Initialize encoder with default codec (H.265)
    /// </summary>
    public bool Initialize(int width, int height, int fps, int bitrate, ID3D11Device device)
    {
        return Initialize(width, height, fps, bitrate, device, VideoCodec.H265);
    }

    /// <summary>
    /// Initialize encoder with specific codec preference
    /// </summary>
    public bool Initialize(int width, int height, int fps, int bitrate, ID3D11Device device, VideoCodec preferredCodec)
    {
        if (_encoder != null) return true;

        try
        {
            _width = width;
            _height = height;
            _fps = fps;
            _bitrate = bitrate;
            _device = device;
            _context = device.ImmediateContext;
            _preferredCodec = preferredCodec;

            Console.WriteLine($"[LibAvEncoderAdapter] Initializing {width}x{height} @ {fps}fps, {bitrate}kbps, codec={preferredCodec}");

            // Create staging texture for CPU read
            _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
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

            // Allocate NV12 buffer (Y plane + UV plane)
            _nv12Buffer = new byte[width * height * 3 / 2];

            _encoder = new LibAvEncoder(width, height, fps, bitrate * 1000, device, preferredCodec);
            _encoder.OnEncodedData += OnEncoderData;

            if (!_encoder.Initialize())
            {
                Console.WriteLine("[LibAvEncoderAdapter] LibAvEncoder initialization failed");
                Cleanup();
                return false;
            }

            Console.WriteLine($"[LibAvEncoderAdapter] Initialized successfully with codec: {_encoder.CurrentCodec}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoderAdapter] Initialize exception: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    private void OnEncoderData(byte[] nalData, bool isKeyFrame, long pts)
    {
        OnEncodedData?.Invoke(nalData, isKeyFrame, pts);
    }

    public bool EncodeTexture(ID3D11Texture2D texture, bool forceKeyframe = false)
    {
        if (_encoder == null || _disposed) 
            return false;
        
        try
        {
            _frameCount++;
            
            // Use TRUE ZERO-COPY path if available (D3D11VA mode)
            if (_encoder.SupportsZeroCopyTexture)
            {
                return _encoder.EncodeD3D11TextureZeroCopy(texture, forceKeyframe);
            }
            
            // Fallback: CPU copy path (for CUDA mode or when zero-copy not available)
            if (_stagingTexture == null || _nv12Buffer == null || _context == null)
                return false;
            
            // Copy input texture to staging texture
            _context.CopyResource(_stagingTexture, texture);
            
            // Map staging texture for CPU read
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            
            try
            {
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    int srcPitch = (int)mapped.RowPitch;
                    
                    fixed (byte* dst = _nv12Buffer)
                    {
                        // Copy Y plane
                        byte* dstY = dst;
                        for (int y = 0; y < _height; y++)
                        {
                            Buffer.MemoryCopy(src + y * srcPitch, dstY + y * _width, _width, _width);
                        }
                        
                        // Copy UV plane (after Y plane in NV12)
                        byte* srcUV = src + srcPitch * _height;
                        byte* dstUV = dst + _width * _height;
                        int uvHeight = _height / 2;
                        for (int y = 0; y < uvHeight; y++)
                        {
                            Buffer.MemoryCopy(srcUV + y * srcPitch, dstUV + y * _width, _width, _width);
                        }
                    }
                }
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
            
            // Encode NV12 bytes
            return _encoder.EncodeNV12(_nv12Buffer, _width, _height, forceKeyframe);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoderAdapter] EncodeTexture exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Encode NV12 bytes directly (used for NVIDIA compatibility path)
    /// </summary>
    public bool EncodeNV12Bytes(byte[] nv12Bytes, int width, int height, bool forceKeyframe = false)
    {
        if (_encoder == null || _disposed)
            return false;
        
        try
        {
            _frameCount++;
            return _encoder.EncodeNV12(nv12Bytes, width, height, forceKeyframe);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoderAdapter] EncodeNV12Bytes exception: {ex.Message}");
            return false;
        }
    }

    public void Flush()
    {
        _encoder?.Flush();
    }

    /// <summary>
    /// Dynamically change encoder bitrate
    /// </summary>
    public bool SetBitrate(int bitrateKbps)
    {
        if (_encoder == null) return false;

        bool result = _encoder.SetBitrate(bitrateKbps);
        if (result)
        {
            _bitrate = bitrateKbps;
        }
        return result;
    }

    /// <summary>
    /// Dynamically change encoder FPS.
    /// NOTE: LibAv encoder doesn't reliably support runtime FPS changes.
    /// This method returns false to indicate the feature is not supported.
    /// </summary>
    public bool SetFps(int fps)
    {
        // LibAv encoder doesn't support runtime FPS changes without re-initialization
        Console.WriteLine($"[LibAvEncoderAdapter] Runtime FPS change not supported (requested: {fps}fps)");
        return false;
    }

    private void Cleanup()
    {
        if (_encoder != null)
        {
            _encoder.OnEncodedData -= OnEncoderData;
            _encoder.Dispose();
            _encoder = null;
        }
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _nv12Buffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Cleanup();
        
        Console.WriteLine("[LibAvEncoderAdapter] Disposed");
    }
}
