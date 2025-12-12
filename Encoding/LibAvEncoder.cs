#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.DXGI;
using SharpGen.Runtime;
// Use Vortice D3D11 types explicitly to avoid conflict with FFmpeg.AutoGen
using D3D11Device = Vortice.Direct3D11.ID3D11Device;
using D3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using D3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using Vortice.Direct3D11;
using FFmpegD3D11Device = FFmpeg.AutoGen.ID3D11Device;

namespace RemotePlayServer.Encoding;

/// <summary>
/// Hardware-accelerated H.264 encoder using FFmpeg libavcodec with D3D11VA.
/// Provides zero-copy encoding from D3D11 textures to H.264 NAL units.
/// </summary>
public unsafe class LibAvEncoder : IDisposable
{
    private AVCodecContext* _codecCtx;
    private AVBufferRef* _hwDeviceCtx;
    private AVBufferRef* _hwFramesCtx;
    private AVFrame* _hwFrame;
    private AVPacket* _packet;
    
    private D3D11Device? _device;
    private D3D11DeviceContext? _context;
    private D3D11Texture2D? _stagingTexture;
    
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private long _frameCount;
    private bool _disposed;
    private bool _initialized;
    private bool _useHardwareFrames;
    
    private readonly object _lock = new();

    public bool IsInitialized => _initialized;
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Event fired when encoded H.264 data is available.
    /// Parameters: (byte[] nalData, bool isKeyFrame, long pts)
    /// </summary>
    public event Action<byte[], bool, long>? OnEncodedData;

    static LibAvEncoder()
    {
        // Set FFmpeg DLL search path
        var ffmpegPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "bin");
        
        if (System.IO.Directory.Exists(ffmpegPath))
        {
            ffmpeg.RootPath = ffmpegPath;
            Console.WriteLine($"[LibAvEncoder] FFmpeg path: {ffmpegPath}");
        }
        else
        {
            // Try shared folder
            var sharedPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "ffmpeg-master-latest-win64-gpl-shared", "bin");
            if (System.IO.Directory.Exists(sharedPath))
            {
                ffmpeg.RootPath = sharedPath;
                Console.WriteLine($"[LibAvEncoder] FFmpeg path: {sharedPath}");
            }
        }
    }

    public LibAvEncoder(int width, int height, int fps, int bitrate, D3D11Device device)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _bitrate = bitrate;
        _device = device;
        _context = _device.ImmediateContext;
    }

    /// <summary>
    /// Initialize the encoder with D3D11VA hardware acceleration.
    /// </summary>
    public bool Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return true;
            
            try
            {
                // Find h264_nvenc encoder (prefer NVENC for NVIDIA)
                AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
                if (codec == null)
                {
                    Console.WriteLine("[LibAvEncoder] h264_nvenc not found, trying h264_amf");
                    codec = ffmpeg.avcodec_find_encoder_by_name("h264_amf");
                }
                if (codec == null)
                {
                    Console.WriteLine("[LibAvEncoder] h264_amf not found, trying h264_qsv");
                    codec = ffmpeg.avcodec_find_encoder_by_name("h264_qsv");
                }
                if (codec == null)
                {
                    Console.WriteLine("[LibAvEncoder] No hardware encoder found!");
                    return false;
                }
                
                string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";
                Console.WriteLine($"[LibAvEncoder] Using encoder: {codecName}");

                // Allocate codec context
                _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecCtx == null)
                {
                    Console.WriteLine("[LibAvEncoder] Failed to allocate codec context");
                    return false;
                }

                // Configure encoder
                _codecCtx->width = _width;
                _codecCtx->height = _height;
                _codecCtx->time_base = new AVRational { num = 1, den = _fps };
                _codecCtx->framerate = new AVRational { num = _fps, den = 1 };
                _codecCtx->bit_rate = _bitrate;
                _codecCtx->gop_size = _fps * 2; // Keyframe every 2 seconds
                _codecCtx->max_b_frames = 0; // No B-frames for low latency
                _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                
                // Set low-latency options for NVENC
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "p1", 0); // Fastest preset (NVENC)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "tune", "ull", 0); // Ultra low latency
                ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "cbr", 0); // Constant bitrate
                ffmpeg.av_opt_set(_codecCtx->priv_data, "zerolatency", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "delay", "0", 0); // No encoder delay
                ffmpeg.av_opt_set(_codecCtx->priv_data, "rc-lookahead", "0", 0); // No lookahead
                ffmpeg.av_opt_set(_codecCtx->priv_data, "spatial-aq", "0", 0); // Disable spatial AQ
                ffmpeg.av_opt_set(_codecCtx->priv_data, "temporal-aq", "0", 0); // Disable temporal AQ
                ffmpeg.av_opt_set(_codecCtx->priv_data, "b_adapt", "0", 0); // No B-frame adaptation
                ffmpeg.av_opt_set(_codecCtx->priv_data, "no-scenecut", "1", 0); // Disable scene cut detection
                
                // Force immediate output - no internal buffering
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                _codecCtx->thread_count = 1; // Single thread for lowest latency
                
                // Try to create D3D11VA hardware device context
                _useHardwareFrames = InitializeHardwareContext();
                if (!_useHardwareFrames)
                {
                    Console.WriteLine("[LibAvEncoder] D3D11VA init failed, using software upload");
                }

                // Open codec
                int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
                if (ret < 0)
                {
                    Console.WriteLine($"[LibAvEncoder] Failed to open codec: {GetErrorMessage(ret)}");
                    return false;
                }

                // Allocate packet
                _packet = ffmpeg.av_packet_alloc();
                if (_packet == null)
                {
                    Console.WriteLine("[LibAvEncoder] Failed to allocate packet");
                    return false;
                }

                // Allocate frame based on mode
                _hwFrame = ffmpeg.av_frame_alloc();
                if (_hwFrame == null)
                {
                    Console.WriteLine("[LibAvEncoder] Failed to allocate frame");
                    return false;
                }

                if (_useHardwareFrames)
                {
                    // Get hardware frame from pool
                    ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
                    if (ret < 0)
                    {
                        Console.WriteLine($"[LibAvEncoder] Failed to get hw frame buffer: {GetErrorMessage(ret)}");
                        // Fall back to software mode
                        _useHardwareFrames = false;
                    }
                    else
                    {
                        Console.WriteLine("[LibAvEncoder] Using D3D11 hardware frames (zero-copy)");
                    }
                }
                
                if (!_useHardwareFrames)
                {
                    // Software frame setup
                    _hwFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                    _hwFrame->width = _width;
                    _hwFrame->height = _height;
                    
                    ret = ffmpeg.av_frame_get_buffer(_hwFrame, 32);
                    if (ret < 0)
                    {
                        Console.WriteLine($"[LibAvEncoder] Failed to allocate frame buffer: {GetErrorMessage(ret)}");
                        return false;
                    }
                }

                // Create staging texture for NV12 input (software mode only)
                if (!_useHardwareFrames)
                {
                    CreateStagingTexture();
                }

                _initialized = true;
                var modeStr = _useHardwareFrames ? "D3D11 zero-copy" : "software upload";
                Console.WriteLine($"[LibAvEncoder] Initialized {_width}x{_height} @ {_fps}fps, {_bitrate/1000}kbps ({modeStr})");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LibAvEncoder] Init exception: {ex.Message}");
                return false;
            }
        }
    }

    private bool InitializeHardwareContext()
    {
        // Try CUDA first (best for NVENC on NVIDIA cards)
        if (TryInitializeCuda())
        {
            return true;
        }
        
        // Try D3D11VA as fallback
        if (TryInitializeD3D11VA())
        {
            return true;
        }
        
        // Fall back to software upload (still uses NVENC for encoding)
        Console.WriteLine("[LibAvEncoder] Hardware frames init failed, using software upload (NVENC still active)");
        return false;
    }
    
    private bool TryInitializeCuda()
    {
        try
        {
            // Create CUDA hardware device context
            AVBufferRef* hwDeviceCtx = null;
            int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, null, null, 0);
            if (ret < 0)
            {
                Console.WriteLine($"[LibAvEncoder] CUDA device not available: {GetErrorMessage(ret)}");
                return false;
            }
            _hwDeviceCtx = hwDeviceCtx;
            
            // Create hardware frames context for CUDA
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Console.WriteLine("[LibAvEncoder] Failed to allocate CUDA frames context");
                CleanupHwContext();
                return false;
            }
            
            // Configure CUDA frames context
            AVHWFramesContext* framesCtx = (AVHWFramesContext*)_hwFramesCtx->data;
            framesCtx->format = AVPixelFormat.AV_PIX_FMT_CUDA;
            framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            framesCtx->width = _width;
            framesCtx->height = _height;
            framesCtx->initial_pool_size = 4;
            
            ret = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
            if (ret < 0)
            {
                Console.WriteLine($"[LibAvEncoder] Failed to init CUDA frames context: {GetErrorMessage(ret)}");
                CleanupHwContext();
                return false;
            }
            
            // Set encoder to use CUDA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_CUDA;
            
            Console.WriteLine("[LibAvEncoder] CUDA hardware context initialized (zero-copy enabled)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoder] CUDA init exception: {ex.Message}");
            CleanupHwContext();
            return false;
        }
    }
    
    private bool TryInitializeD3D11VA()
    {
        try
        {
            // Create D3D11VA hardware device context
            AVBufferRef* hwDeviceCtx = null;
            int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
            if (ret < 0)
            {
                Console.WriteLine($"[LibAvEncoder] D3D11VA device not available: {GetErrorMessage(ret)}");
                return false;
            }
            _hwDeviceCtx = hwDeviceCtx;
            
            // Create hardware frames context
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Console.WriteLine("[LibAvEncoder] Failed to allocate D3D11VA frames context");
                CleanupHwContext();
                return false;
            }
            
            // Configure D3D11VA frames context
            AVHWFramesContext* framesCtx = (AVHWFramesContext*)_hwFramesCtx->data;
            framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            framesCtx->width = _width;
            framesCtx->height = _height;
            framesCtx->initial_pool_size = 4;
            
            ret = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
            if (ret < 0)
            {
                Console.WriteLine($"[LibAvEncoder] Failed to init D3D11VA frames context: {GetErrorMessage(ret)}");
                CleanupHwContext();
                return false;
            }
            
            // Set encoder to use D3D11VA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            
            Console.WriteLine("[LibAvEncoder] D3D11VA hardware context initialized (zero-copy enabled)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoder] D3D11VA init exception: {ex.Message}");
            CleanupHwContext();
            return false;
        }
    }
    
    private void CleanupHwContext()
    {
        if (_hwFramesCtx != null)
        {
            AVBufferRef* temp = _hwFramesCtx;
            ffmpeg.av_buffer_unref(&temp);
            _hwFramesCtx = null;
        }
        if (_hwDeviceCtx != null)
        {
            AVBufferRef* temp = _hwDeviceCtx;
            ffmpeg.av_buffer_unref(&temp);
            _hwDeviceCtx = null;
        }
    }

    private void CreateStagingTexture()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        });
    }

    /// <summary>
    /// Encode a frame from NV12 byte array (from GpuColorConverter).
    /// </summary>
    public bool EncodeNV12(byte[] nv12Data, int width, int height)
    {
        if (!_initialized || _disposed) return false;
        
        lock (_lock)
        {
            try
            {
                // Handle resolution change
                if (width != _width || height != _height)
                {
                    Console.WriteLine($"[LibAvEncoder] Resolution changed {_width}x{_height} -> {width}x{height}");
                    // Would need to reinitialize encoder for resolution change
                    return false;
                }

                int ret;
                
                if (_useHardwareFrames)
                {
                    // Hardware frames mode: upload NV12 to D3D11 texture
                    // Create a software frame to hold the NV12 data
                    AVFrame* swFrame = ffmpeg.av_frame_alloc();
                    if (swFrame == null)
                    {
                        Console.WriteLine("[LibAvEncoder] Failed to allocate sw frame");
                        return false;
                    }
                    
                    try
                    {
                        swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                        swFrame->width = _width;
                        swFrame->height = _height;
                        
                        ret = ffmpeg.av_frame_get_buffer(swFrame, 32);
                        if (ret < 0)
                        {
                            Console.WriteLine($"[LibAvEncoder] Failed to alloc sw frame buffer: {GetErrorMessage(ret)}");
                            return false;
                        }
                        
                        // Copy NV12 data to software frame
                        int yPlaneSize = _width * _height;
                        fixed (byte* srcY = nv12Data)
                        {
                            byte* dstY = swFrame->data[0];
                            int dstStrideY = swFrame->linesize[0];
                            for (int y = 0; y < _height; y++)
                            {
                                Buffer.MemoryCopy(srcY + y * _width, dstY + y * dstStrideY, _width, _width);
                            }
                        }
                        fixed (byte* srcUV = &nv12Data[yPlaneSize])
                        {
                            byte* dstUV = swFrame->data[1];
                            int dstStrideUV = swFrame->linesize[1];
                            int uvHeight = _height / 2;
                            for (int y = 0; y < uvHeight; y++)
                            {
                                Buffer.MemoryCopy(srcUV + y * _width, dstUV + y * dstStrideUV, _width, _width);
                            }
                        }
                        
                        // Transfer software frame to hardware frame
                        ret = ffmpeg.av_hwframe_transfer_data(_hwFrame, swFrame, 0);
                        if (ret < 0)
                        {
                            Console.WriteLine($"[LibAvEncoder] HW transfer failed: {GetErrorMessage(ret)}");
                            return false;
                        }
                        
                        _hwFrame->pts = _frameCount++;
                    }
                    finally
                    {
                        AVFrame* tempFrame = swFrame;
                        ffmpeg.av_frame_free(&tempFrame);
                    }
                }
                else
                {
                    // Software mode: copy directly to frame buffer
                    ret = ffmpeg.av_frame_make_writable(_hwFrame);
                    if (ret < 0)
                    {
                        Console.WriteLine($"[LibAvEncoder] Frame not writable: {GetErrorMessage(ret)}");
                        return false;
                    }

                    // Copy NV12 data to frame
                    int yPlaneSize = _width * _height;
                    
                    // Y plane
                    fixed (byte* srcY = nv12Data)
                    {
                        byte* dstY = _hwFrame->data[0];
                        int dstStrideY = _hwFrame->linesize[0];
                        
                        for (int y = 0; y < _height; y++)
                        {
                            Buffer.MemoryCopy(srcY + y * _width, dstY + y * dstStrideY, _width, _width);
                        }
                    }

                    // UV plane
                    fixed (byte* srcUV = &nv12Data[yPlaneSize])
                    {
                        byte* dstUV = _hwFrame->data[1];
                        int dstStrideUV = _hwFrame->linesize[1];
                        int uvHeight = _height / 2;
                        
                        for (int y = 0; y < uvHeight; y++)
                        {
                            Buffer.MemoryCopy(srcUV + y * _width, dstUV + y * dstStrideUV, _width, _width);
                        }
                    }
                    
                    _hwFrame->pts = _frameCount++;
                }

                // Send frame to encoder
                ret = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
                if (ret < 0)
                {
                    Console.WriteLine($"[LibAvEncoder] Send frame error: {GetErrorMessage(ret)}");
                    return false;
                }

                // Receive encoded packets
                while (true)
                {
                    ret = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;
                    if (ret < 0)
                    {
                        Console.WriteLine($"[LibAvEncoder] Receive packet error: {GetErrorMessage(ret)}");
                        break;
                    }

                    // Extract NAL data
                    byte[] nalData = new byte[_packet->size];
                    Marshal.Copy((IntPtr)_packet->data, nalData, 0, _packet->size);
                    
                    bool isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    
                    // Fire event
                    OnEncodedData?.Invoke(nalData, isKeyFrame, _packet->pts);

                    ffmpeg.av_packet_unref(_packet);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LibAvEncoder] Encode exception: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Encode directly from D3D11 NV12 texture (zero-copy path).
    /// </summary>
    public bool EncodeTexture(D3D11Texture2D nv12Texture)
    {
        if (!_initialized || _disposed) return false;
        
        lock (_lock)
        {
            try
            {
                // Copy texture to staging
                _context!.CopyResource(_stagingTexture!, nv12Texture);
                
                // Map staging texture
                var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                
                try
                {
                    // Make frame writable
                    int ret = ffmpeg.av_frame_make_writable(_hwFrame);
                    if (ret < 0) return false;

                    // Copy from mapped texture to AVFrame
                    byte* src = (byte*)mapped.DataPointer;
                    int srcPitch = (int)mapped.RowPitch;
                    
                    // Y plane
                    byte* dstY = _hwFrame->data[0];
                    int dstStrideY = _hwFrame->linesize[0];
                    for (int y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(
                            src + y * srcPitch,
                            dstY + y * dstStrideY,
                            _width, _width);
                    }
                    
                    // UV plane (after Y plane in NV12 texture)
                    byte* srcUV = src + srcPitch * _height;
                    byte* dstUV = _hwFrame->data[1];
                    int dstStrideUV = _hwFrame->linesize[1];
                    int uvHeight = _height / 2;
                    for (int y = 0; y < uvHeight; y++)
                    {
                        Buffer.MemoryCopy(
                            srcUV + y * srcPitch,
                            dstUV + y * dstStrideUV,
                            _width, _width);
                    }
                }
                finally
                {
                    _context.Unmap(_stagingTexture!, 0);
                }

                // Set PTS
                _hwFrame->pts = _frameCount++;

                // Send frame to encoder
                int sendRet = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
                if (sendRet < 0) return false;

                // Receive encoded packets
                while (true)
                {
                    int recvRet = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                    if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recvRet == ffmpeg.AVERROR_EOF)
                        break;
                    if (recvRet < 0) break;

                    byte[] nalData = new byte[_packet->size];
                    Marshal.Copy((IntPtr)_packet->data, nalData, 0, _packet->size);
                    
                    bool isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    OnEncodedData?.Invoke(nalData, isKeyFrame, _packet->pts);

                    ffmpeg.av_packet_unref(_packet);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LibAvEncoder] EncodeTexture exception: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Flush encoder to get remaining frames.
    /// </summary>
    public void Flush()
    {
        if (!_initialized || _disposed) return;
        
        lock (_lock)
        {
            // Send null frame to flush
            ffmpeg.avcodec_send_frame(_codecCtx, null);
            
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;
                if (ret < 0) break;

                byte[] nalData = new byte[_packet->size];
                Marshal.Copy((IntPtr)_packet->data, nalData, 0, _packet->size);
                
                bool isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                OnEncodedData?.Invoke(nalData, isKeyFrame, _packet->pts);

                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    private static string GetErrorMessage(int error)
    {
        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(error, buffer, 1024);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Error {error}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            if (_packet != null)
            {
                fixed (AVPacket** p = &_packet)
                    ffmpeg.av_packet_free(p);
            }
            
            if (_hwFrame != null)
            {
                fixed (AVFrame** f = &_hwFrame)
                    ffmpeg.av_frame_free(f);
            }
            
            if (_codecCtx != null)
            {
                fixed (AVCodecContext** c = &_codecCtx)
                    ffmpeg.avcodec_free_context(c);
            }
            
            if (_hwFramesCtx != null)
            {
                fixed (AVBufferRef** b = &_hwFramesCtx)
                    ffmpeg.av_buffer_unref(b);
            }
            
            if (_hwDeviceCtx != null)
            {
                fixed (AVBufferRef** b = &_hwDeviceCtx)
                    ffmpeg.av_buffer_unref(b);
            }
            
            _stagingTexture?.Dispose();
            _context?.Dispose();
        }
        
        Console.WriteLine("[LibAvEncoder] Disposed");
    }
}
