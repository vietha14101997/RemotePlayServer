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
/// GPU Vendor enumeration for encoder selection
/// </summary>
enum GpuVendorType { Unknown, NVIDIA, AMD, Intel }

/// <summary>
/// Hardware-accelerated H.264 encoder using FFmpeg libavcodec with D3D11VA.
/// Provides zero-copy encoding from D3D11 textures to H.264 NAL units.
/// Supports NVIDIA NVENC (via CUDA), AMD AMF (via D3D11VA), and Intel QSV.
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
    
    // FFmpeg D3D11 device/context for zero-copy (when using D3D11VA mode)
    private FFmpegD3D11Device* _ffmpegD3D11Device;
    private FFmpeg.AutoGen.ID3D11DeviceContext* _ffmpegD3D11Context;
    private bool _isD3D11VAMode;
    
    // QSV Specific: Frames context for the encoder (derived from D3D11)
    private AVBufferRef* _qsvFramesCtx;
    
    // For Cross-Device Bridging (when Encoder uses a different device than Capture)
    private D3D11Device? _encoderD3D11Device;
    private D3D11DeviceContext? _encoderD3D11Context;
    private D3D11Texture2D? _sharedBridgeTexture; // On Capture Device
    private D3D11Texture2D? _importedBridgeTexture; // On Encoder Device
    private IntPtr _lastSharedHandle = IntPtr.Zero;
    private bool _usingCrossDeviceBridge;
    
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private long _frameCount;
    private bool _disposed;
    private bool _initialized;
    private bool _useHardwareFrames;
    private GpuVendorType _gpuVendor = GpuVendorType.Unknown;
    private string _encoderName = "unknown";
    
    private readonly object _lock = new();

    public bool IsInitialized => _initialized;
    public int Width => _width;
    public int Height => _height;
    
    /// <summary>
    /// True if encoder supports true zero-copy D3D11 texture encoding (D3D11VA mode)
    /// </summary>
    public bool SupportsZeroCopyTexture => _isD3D11VAMode && _useHardwareFrames;

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
    /// Detect GPU vendor from D3D11 device or DXGI factory
    /// </summary>
    private GpuVendorType DetectGpuVendor()
    {
        if (_gpuVendor != GpuVendorType.Unknown) return _gpuVendor;
        
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure) break;
                var desc = adapter.Description;
                adapter.Dispose();
                
                string name = desc.Description.ToUpperInvariant();
                // Skip Microsoft Basic Render Driver
                if (name.Contains("MICROSOFT") || name.Contains("BASIC")) continue;
                
                if (name.Contains("NVIDIA") || name.Contains("GEFORCE") || name.Contains("GTX") || name.Contains("RTX"))
                {
                    _gpuVendor = GpuVendorType.NVIDIA;
                    Console.WriteLine($"[LibAvEncoder] Detected GPU: NVIDIA ({desc.Description})");
                    return _gpuVendor;
                }
                if (name.Contains("AMD") || name.Contains("RADEON") || name.Contains("RX "))
                {
                    _gpuVendor = GpuVendorType.AMD;
                    Console.WriteLine($"[LibAvEncoder] Detected GPU: AMD ({desc.Description})");
                    return _gpuVendor;
                }
                if (name.Contains("INTEL") || name.Contains("UHD") || name.Contains("IRIS"))
                {
                    _gpuVendor = GpuVendorType.Intel;
                    Console.WriteLine($"[LibAvEncoder] Detected GPU: Intel ({desc.Description})");
                    return _gpuVendor;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoder] GPU detection error: {ex.Message}");
        }
        
        Console.WriteLine("[LibAvEncoder] GPU vendor: Unknown");
        return GpuVendorType.Unknown;
    }
    
    /// <summary>
    /// Select optimal encoder based on GPU vendor
    /// </summary>
    private AVCodec* SelectEncoder()
    {
        var vendor = DetectGpuVendor();
        AVCodec* codec = null;
        
        switch (vendor)
        {
            case GpuVendorType.AMD:
                // AMD: Prefer AMF encoder
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_amf");
                if (codec != null) { _encoderName = "h264_amf"; return codec; }
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_qsv");
                if (codec != null) { _encoderName = "h264_qsv"; return codec; }
                break;
                
            case GpuVendorType.NVIDIA:
                // NVIDIA: Prefer NVENC encoder
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
                if (codec != null) { _encoderName = "h264_nvenc"; return codec; }
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_qsv");
                if (codec != null) { _encoderName = "h264_qsv"; return codec; }
                break;
                
            case GpuVendorType.Intel:
                // Intel: Prefer QSV encoder
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_qsv");
                if (codec != null) { _encoderName = "h264_qsv"; return codec; }
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
                if (codec != null) { _encoderName = "h264_nvenc"; return codec; }
                break;
                
            default:
                // Unknown: Try all in order
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
                if (codec != null) { _encoderName = "h264_nvenc"; return codec; }
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_amf");
                if (codec != null) { _encoderName = "h264_amf"; return codec; }
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_qsv");
                if (codec != null) { _encoderName = "h264_qsv"; return codec; }
                break;
        }
        
        return codec;
    }
    
    /// <summary>
    /// Configure encoder options based on encoder type
    /// </summary>
    private void ConfigureEncoderOptions()
    {
        // Common settings
        _codecCtx->width = _width;
        _codecCtx->height = _height;
        _codecCtx->time_base = new AVRational { num = 1, den = _fps };
        _codecCtx->framerate = new AVRational { num = _fps, den = 1 };
        _codecCtx->bit_rate = _bitrate;
        _codecCtx->gop_size = _fps; // Keyframe every 1 second (reduced for lower latency)
        _codecCtx->max_b_frames = 0; // No B-frames for low latency
        _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        
        // Force immediate output - no internal buffering
        _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        // Removing GLOBAL_HEADER as it might be problematic for NVENC in some configs
        // _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        _codecCtx->thread_count = 1; // Single thread for lowest latency
        
        switch (_encoderName)
        {
            case "h264_amf":
                // AMD AMF specific options for ultra low latency
                Console.WriteLine("[LibAvEncoder] Configuring AMD AMF encoder for zero-copy");
                ffmpeg.av_opt_set(_codecCtx->priv_data, "usage", "ultralowlatency", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "quality", "speed", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "cbr", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preanalysis", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "vbaq", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "enforce_hrd", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "filler_data", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "frame_skipping", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "header_insertion_mode", "idr", 0);
                // Set maxrate and bufsize for CBR to work properly
                _codecCtx->rc_max_rate = _bitrate;
                _codecCtx->rc_buffer_size = _bitrate / 10; // 100ms buffer for low latency
                // AMF level for width > 2048
                if (_width > 2048)
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "level", "5.1", 0);
                break;
                
            case "h264_nvenc":
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "p1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "tune", "ull", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "cbr", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "zerolatency", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "delay", "0", 0);
                // Remove forced-idr as it might cause invalid param on some drivers
                break;
                
            case "h264_qsv":
                // Intel QSV specific options for ULTRA LOW LATENCY
                Console.WriteLine("[LibAvEncoder] Configuring Intel QSV encoder (Ultra Low Latency)");
                // Fastest preset
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "veryfast", 0);
                // Async depth = 1 means encoder waits for each frame (minimal buffering)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "async_depth", "1", 0);
                // No look-ahead to avoid buffering future frames
                ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead", "0", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead_depth", "0", 0);
                // Low power mode for fixed-function encoder (lower latency)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "low_power", "1", 0);
                // Force single NAL per frame for lower decoding latency
                ffmpeg.av_opt_set(_codecCtx->priv_data, "single_sei_nal_unit", "1", 0);
                // Rate control: VCM (Video Conferencing Mode) optimized for low latency
                ffmpeg.av_opt_set(_codecCtx->priv_data, "rdo", "0", 0);
                // Reduce RC buffer for faster bitrate response
                // Reduce RC buffer for faster bitrate response
                _codecCtx->rc_buffer_size = _bitrate / 10; // 100ms buffer (matched with AMF/NVENC logic)
                _codecCtx->rc_max_rate = (long)(_bitrate * 1.5); // Allow some headroom
                
                // Extra QSV options for stability
                ffmpeg.av_opt_set(_codecCtx->priv_data, "idr_interval", "0", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "pic_timing_sei", "0", 0);
                break;
        }
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
                // Select encoder based on GPU vendor
                AVCodec* codec = SelectEncoder();
                if (codec == null)
                {
                    Console.WriteLine("[LibAvEncoder] No hardware encoder found!");
                    return false;
                }
                
                Console.WriteLine($"[LibAvEncoder] Using encoder: {_encoderName}");

                // Allocate codec context
                _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecCtx == null)
                {
                    Console.WriteLine("[LibAvEncoder] Failed to allocate codec context");
                    return false;
                }

                // Default: Encoder uses the same device as capture
                _encoderD3D11Device = _device;
                _encoderD3D11Context = _context;
                _usingCrossDeviceBridge = false;

                // Configure encoder with vendor-specific options
                ConfigureEncoderOptions();
                
                // Try to create hardware device context based on GPU vendor
                _useHardwareFrames = InitializeHardwareContext();
                if (!_useHardwareFrames)
                {
                    Console.WriteLine($"[LibAvEncoder] Hardware frames init failed for {_encoderName}, using software upload");
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
        var vendor = DetectGpuVendor();
        
        switch (vendor)
        {
            case GpuVendorType.NVIDIA:
                // NVIDIA: Use D3D11VA for true zero-copy from D3D11 textures
                // D3D11VA allows direct texture copy without CPU roundtrip
                Console.WriteLine("[LibAvEncoder] NVIDIA GPU: Trying D3D11VA for zero-copy texture encoding");
                if (TryInitializeNvencD3D11VA())
                    return true;
                Console.WriteLine("[LibAvEncoder] NVIDIA: D3D11VA failed, trying CUDA (will require CPU copy)");
                if (TryInitializeCuda())
                    return true;
                break;
                
            case GpuVendorType.AMD:
                // AMD: Use D3D11VA directly with AMF encoder
                // CUDA is not available on AMD, skip it
                Console.WriteLine("[LibAvEncoder] AMD GPU: Trying D3D11VA hardware context for AMF");
                if (TryInitializeAMFD3D11())
                    return true;
                Console.WriteLine("[LibAvEncoder] AMD: D3D11VA failed, trying generic D3D11VA");
                if (TryInitializeD3D11VA())
                    return true;
                break;
                
            case GpuVendorType.Intel:
                // Intel: Try QSV-specific hardware context first, then D3D11VA fallback
                Console.WriteLine("[LibAvEncoder] Intel GPU: Trying QSV hardware context");
                if (TryInitializeQSV())
                    return true;
                Console.WriteLine("[LibAvEncoder] Intel: QSV failed, trying generic D3D11VA");
                if (TryInitializeD3D11VA())
                    return true;
                break;
                
            default:
                // Unknown: Try all in order
                if (TryInitializeCuda())
                    return true;
                if (TryInitializeD3D11VA())
                    return true;
                break;
        }
        
        Console.WriteLine($"[LibAvEncoder] Hardware frames init failed, using software upload ({_encoderName} still active)");
        return false;
    }
    
    /// <summary>
    /// Initialize D3D11VA hardware context for NVIDIA NVENC.
    /// This enables true zero-copy encoding from D3D11 textures.
    /// </summary>
    private bool TryInitializeNvencD3D11VA()
    {
        try
        {
            Console.WriteLine("[LibAvEncoder] Initializing NVENC with D3D11VA for zero-copy...");
            
            // Try to use shared device if available (ENABLES TRUE ZERO-COPY)
            if (_device != null && _device.NativePointer != IntPtr.Zero)
            {
                Console.WriteLine($"[LibAvEncoder] Using shared D3D11 device for NVENC: 0x{_device.NativePointer:X}");
                
                AVBufferRef* hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                if (hwDeviceCtx != null)
                {
                    AVHWDeviceContext* deviceContext = (AVHWDeviceContext*)hwDeviceCtx->data;
                    AVD3D11VADeviceContext* d3d11vaDeviceCtx = (AVD3D11VADeviceContext*)deviceContext->hwctx;
                    d3d11vaDeviceCtx->device = (FFmpegD3D11Device*)_device.NativePointer;
                    
                    int initRet = ffmpeg.av_hwdevice_ctx_init(hwDeviceCtx);
                    if (initRet >= 0)
                    {
                        _hwDeviceCtx = hwDeviceCtx;
                        Console.WriteLine("[LibAvEncoder] NVENC D3D11VA device context initialized with SHARED device");
                    }
                    else
                    {
                        Console.WriteLine($"[LibAvEncoder] Shared device init failed: {GetErrorMessage(initRet)}");
                        ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    }
                }
            }
            
            // Fallback to creating new device if shared failed
            if (_hwDeviceCtx == null)
            {
                Console.WriteLine("[LibAvEncoder] Accessing shared device failed or not available, creating new D3D11VA device...");
                AVBufferRef* hwDeviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (ret < 0)
                {
                    Console.WriteLine($"[LibAvEncoder] D3D11VA device not available for NVENC: {GetErrorMessage(ret)}");
                    return false;
                }
                _hwDeviceCtx = hwDeviceCtx;
            }
            
            // Create hardware frames context
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Console.WriteLine("[LibAvEncoder] Failed to allocate D3D11VA frames context for NVENC");
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
            
            // Set bind flags for NVENC compatibility
            AVD3D11VAFramesContext* d3d11vaFramesCtx = (AVD3D11VAFramesContext*)framesCtx->hwctx;
            if (d3d11vaFramesCtx != null)
            {
                d3d11vaFramesCtx->BindFlags = (uint)(BindFlags.RenderTarget | BindFlags.ShaderResource);
                d3d11vaFramesCtx->MiscFlags = 0;
            }
            
            int ret2 = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
            if (ret2 < 0)
            {
                Console.WriteLine($"[LibAvEncoder] Failed to init D3D11VA frames context for NVENC: {GetErrorMessage(ret2)}");
                CleanupHwContext();
                return false;
            }
            
            // Set encoder to use D3D11VA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            
            // Store the D3D11 device context from FFmpeg for zero-copy (if we created it)
            // Or access the shared one
            AVHWDeviceContext* devCtx = (AVHWDeviceContext*)_hwDeviceCtx->data;
            AVD3D11VADeviceContext* d3d11vaDevCtx = (AVD3D11VADeviceContext*)devCtx->hwctx;
            _ffmpegD3D11Device = d3d11vaDevCtx->device;
            _ffmpegD3D11Context = d3d11vaDevCtx->device_context;
            
            // If using shared device, _ffmpegD3D11Context might be null if FFmpeg didn't create it?
            // Actually, if we provide the device, FFmpeg doesn't create a context automatically?
            // av_hwdevice_ctx_init docs say for D3D11VA: "The user must provide a ID3D11Device."
            // "If the user does not provide a ID3D11DeviceContext, one will be created."
            // So it should be fine.
            
            Console.WriteLine("[LibAvEncoder] NVENC D3D11VA initialized (TRUE ZERO-COPY enabled)");
            _isD3D11VAMode = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoder] NVENC D3D11VA init exception: {ex.Message}");
            CleanupHwContext();
            return false;
        }
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
    
    /// <summary>
    /// Initialize QSV-specific hardware context for Intel GPU.
    /// QSV encoder requires AV_HWDEVICE_TYPE_QSV device context.
    /// </summary>
    private bool TryInitializeQSV()
    {
        try
        {
            Console.WriteLine("[LibAvEncoder] Initializing Intel QSV hardware context (Enhanced Zero-Copy)...");
            
            AVBufferRef* hwDeviceCtx = null;
            AVBufferRef* d3d11vaDeviceRef = null; // Assert: We must hold a ref to the base device
            bool usedSharedDevice = false;

            // 1. Try to use SHARED D3D11 DEVICE first (Best for Zero-Copy)
            if (_device != null && _device.NativePointer != IntPtr.Zero)
            {
                Console.WriteLine($"[LibAvEncoder] QSV: Attempting to use shared D3D11 device: 0x{_device.NativePointer:X}");
                
                // Create D3D11VA wrapper for our device
                AVBufferRef* d3d11vaDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                if (d3d11vaDevice != null)
                {
                    AVHWDeviceContext* deviceContext = (AVHWDeviceContext*)d3d11vaDevice->data;
                    AVD3D11VADeviceContext* d3d11vaDeviceCtx = (AVD3D11VADeviceContext*)deviceContext->hwctx;
                    d3d11vaDeviceCtx->device = (FFmpegD3D11Device*)_device.NativePointer;
                    
                    // Init the wrapper
                    int initRet = ffmpeg.av_hwdevice_ctx_init(d3d11vaDevice);
                    if (initRet >= 0)
                    {
                        // Derive QSV device from this D3D11VA device
                        int deriveRet = ffmpeg.av_hwdevice_ctx_create_derived(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, d3d11vaDevice, 0);
                        if (deriveRet >= 0)
                        {
                            Console.WriteLine("[LibAvEncoder] QSV device derived from SHARED D3D11VA device successfully");
                            usedSharedDevice = true;
                            // Keep the reference to d3d11vaDevice for frames context creation
                            d3d11vaDeviceRef = ffmpeg.av_buffer_ref(d3d11vaDevice);
                        }
                        else
                        {
                             // -16 (EBUSY) or others are expected if the driver doesn't support sharing in this way
                             Console.WriteLine($"[LibAvEncoder] Shared Device Warning: Failed to derive QSV from shared D3D11VA (Error {deriveRet}). This is normal on some Intel drivers. Switching to Cross-Device Bridge mode.");
                        }
                    }
                    else
                    {
                         Console.WriteLine($"[LibAvEncoder] Failed to init shared D3D11VA wrapper: {GetErrorMessage(initRet)}");
                    }
                    
                    ffmpeg.av_buffer_unref(&d3d11vaDevice);
                }
            }
            
            // 2. Fallback: Create new device and its D3D11VA base
            if (!usedSharedDevice)
            {
                Console.WriteLine("[LibAvEncoder] QSV: Shared device unavailable, creating new D3D11VA base device...");
                
                AVBufferRef* d3d11vaDevice = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&d3d11vaDevice, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (ret < 0)
                {
                    Console.WriteLine($"[LibAvEncoder] D3D11VA base device creation failed: {GetErrorMessage(ret)}");
                    return false;
                }
                
                // Derive QSV device
                ret = ffmpeg.av_hwdevice_ctx_create_derived(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, d3d11vaDevice, 0);
                if (ret < 0)
                {
                    Console.WriteLine($"[LibAvEncoder] Failed to derive QSV from new D3D11VA: {GetErrorMessage(ret)}");
                    ffmpeg.av_buffer_unref(&d3d11vaDevice);
                    return false;
                }
                
                
                d3d11vaDeviceRef = ffmpeg.av_buffer_ref(d3d11vaDevice);
                ffmpeg.av_buffer_unref(&d3d11vaDevice);
                
                // --- CROSS-DEVICE SETUP ---
                // We created a NEW device. We must extract it to use for bridging.
                // QSV Context -> HW Device -> D3D11VA Context -> Device Pointer
                AVHWDeviceContext* qsvDevCtx = (AVHWDeviceContext*)hwDeviceCtx->data;
                // The QSV device context doesn't expose D3D11 device directly easily here?
                // Wait, hwDeviceCtx is QSV type.
                // We need to look at the D3D11VA base.
                // Actually we already have d3d11vaDeviceRef which IS the D3D11VA device!
                
                AVHWDeviceContext* d3d11Ctx = (AVHWDeviceContext*)d3d11vaDeviceRef->data;
                AVD3D11VADeviceContext* d3d11vaCtx = (AVD3D11VADeviceContext*)d3d11Ctx->hwctx;
                
                IntPtr newDevicePtr = (IntPtr)d3d11vaCtx->device;
                Console.WriteLine($"[LibAvEncoder] QSV: Created NEW internal D3D11 Device: 0x{newDevicePtr:X}");
                
                // Create Vortice wrappers for the new device so we can issue Copy commands on it
                _encoderD3D11Device = new D3D11Device(newDevicePtr);
                _encoderD3D11Context = _encoderD3D11Device.ImmediateContext;
                _usingCrossDeviceBridge = true;
                
                Console.WriteLine("[LibAvEncoder] QSV: Enabled Cross-Device Bridge (Shared Handle)");
            }
            
            _hwDeviceCtx = hwDeviceCtx;
            
            // 3. Create Frames Context
            // Improved strategy: Create D3D11VA frames context first (with BindFlags) then derive QSV frames
            // This matches AMF/NVENC logic and ensures textures are compatible
            
            Console.WriteLine("[LibAvEncoder] QSV: Creating D3D11VA frames context explicitely...");
            
            AVBufferRef* d3d11FramesRef = ffmpeg.av_hwframe_ctx_alloc(d3d11vaDeviceRef);
            if (d3d11FramesRef == null)
            {
                 Console.WriteLine("[LibAvEncoder] Failed to alloc D3D11VA frames context");
                 ffmpeg.av_buffer_unref(&d3d11vaDeviceRef);
                 CleanupHwContext();
                 return false;
            }
            
            AVHWFramesContext* framesCtx = (AVHWFramesContext*)d3d11FramesRef->data;
            framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            framesCtx->width = _width;
            framesCtx->height = _height;
            framesCtx->initial_pool_size = 16;
            
            // Set BindFlags (Crucial for QSV/AMF/NVENC compatibility)
            AVD3D11VAFramesContext* d3d11vaFramesCtx = (AVD3D11VAFramesContext*)framesCtx->hwctx;
            d3d11vaFramesCtx->BindFlags = (uint)(Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource);
            d3d11vaFramesCtx->MiscFlags = (uint)Vortice.Direct3D11.ResourceOptionFlags.Shared;
            
            int ctxRet = ffmpeg.av_hwframe_ctx_init(d3d11FramesRef);
            if (ctxRet < 0)
            {
                Console.WriteLine($"[LibAvEncoder] Failed to init D3D11VA frames: {GetErrorMessage(ctxRet)}");
                ffmpeg.av_buffer_unref(&d3d11FramesRef);
                ffmpeg.av_buffer_unref(&d3d11vaDeviceRef);
                CleanupHwContext();
                return false;
            }
            
            // 4. QSV Frames Setup
            // Use Independent QSV Frames by default (Transfer Mode) to avoid "Function not implemented" on Map.
            // Deriving QSV -> D3D11 is brittle on some drivers.
            
            AVBufferRef* qsvFramesRef = null;
            int framesInitRet = -1;

            if (usedSharedDevice)
            {
                 Console.WriteLine("[LibAvEncoder] Attempting to derive QSV frames from D3D11 frames...");
                 framesInitRet = ffmpeg.av_hwframe_ctx_create_derived(&qsvFramesRef, AVPixelFormat.AV_PIX_FMT_QSV, _hwDeviceCtx, d3d11FramesRef, 0);
            }

            if (framesInitRet >= 0)
            {
                Console.WriteLine("[LibAvEncoder] QSV frames derived from D3D11VA frames successfully");
                _qsvFramesCtx = qsvFramesRef; 
            }

            else
            {
                if (usedSharedDevice)
                     Console.WriteLine($"[LibAvEncoder] Failed to derive QSV frames: {GetErrorMessage(framesInitRet)}. Fallback to independent frames.");
                
                // Create Independent QSV Frames Context
                _qsvFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
                var indepFramesCtx = (AVHWFramesContext*)_qsvFramesCtx->data;
                indepFramesCtx->format = AVPixelFormat.AV_PIX_FMT_QSV;
                indepFramesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                indepFramesCtx->width = _width;
                indepFramesCtx->height = _height;
                indepFramesCtx->initial_pool_size = 16;
                
                if (ffmpeg.av_hwframe_ctx_init(_qsvFramesCtx) < 0)
                {
                     Console.WriteLine("[LibAvEncoder] Independent QSV frames init failed");
                     return false;
                }
                Console.WriteLine($"[LibAvEncoder] Initialized Independent QSV Frames (Transfer Mode)");
            }
            
            ffmpeg.av_buffer_unref(&d3d11vaDeviceRef);

            // FIX: Use D3D11 Frames as the PRIMARY hardware frames context for the encoder class.
            // This ensures EncodeD3D11TextureZeroCopy works with D3D11 textures.
            // We basically "hide" QSV frames from the upper logic and only use them at the very end of encoding.
            _hwFramesCtx = ffmpeg.av_buffer_ref(d3d11FramesRef);
            ffmpeg.av_buffer_unref(&d3d11FramesRef);

            // Set encoder to use QSV frames (it demands QSV frames)
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_qsvFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_QSV;
            
            Console.WriteLine($"[LibAvEncoder] Intel QSV hardware context initialized (SharedDevice={usedSharedDevice}, Separation Mode)");
            _isD3D11VAMode = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoder] QSV init exception: {ex.Message}");
            CleanupHwContext();
            return false;
        }
    }
    
    /// <summary>
    /// Initialize D3D11VA hardware context specifically for AMD AMF encoder.
    /// AMF encoder works best with D3D11 hardware frames.
    /// Uses the shared D3D11 device from ClusterCapture for true zero-copy.
    /// </summary>
    private bool TryInitializeAMFD3D11()
    {
        try
        {
            Console.WriteLine("[LibAvEncoder] Initializing AMD AMF D3D11 hardware context with shared device...");
            
            if (_device == null)
            {
                Console.WriteLine("[LibAvEncoder] No shared D3D11 device available, falling back to FFmpeg device");
                return TryInitializeAMFD3D11WithNewDevice();
            }
            
            // Get the native device pointer
            IntPtr devicePtr = _device.NativePointer;
            if (devicePtr == IntPtr.Zero)
            {
                Console.WriteLine("[LibAvEncoder] Failed to get D3D11 device pointer");
                return TryInitializeAMFD3D11WithNewDevice();
            }
            
            Console.WriteLine($"[LibAvEncoder] Using shared D3D11 device: 0x{devicePtr:X}");
            
            // Create D3D11VA hardware device context WITH our existing device
            AVBufferRef* hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (hwDeviceCtx == null)
            {
                Console.WriteLine("[LibAvEncoder] Failed to allocate D3D11VA device context");
                return TryInitializeAMFD3D11WithNewDevice();
            }
            
            // Get the D3D11VA device context data and set our device
            AVHWDeviceContext* deviceContext = (AVHWDeviceContext*)hwDeviceCtx->data;
            AVD3D11VADeviceContext* d3d11vaDeviceCtx = (AVD3D11VADeviceContext*)deviceContext->hwctx;
            
            // Set our shared device - this is the key for zero-copy!
            d3d11vaDeviceCtx->device = (FFmpegD3D11Device*)devicePtr;
            
            // Initialize the device context
            int ret = ffmpeg.av_hwdevice_ctx_init(hwDeviceCtx);
            if (ret < 0)
            {
                Console.WriteLine($"[LibAvEncoder] Failed to init D3D11VA device context with shared device: {GetErrorMessage(ret)}");
                ffmpeg.av_buffer_unref(&hwDeviceCtx);
                return TryInitializeAMFD3D11WithNewDevice();
            }
            _hwDeviceCtx = hwDeviceCtx;
            
            Console.WriteLine("[LibAvEncoder] D3D11VA device context initialized with shared device");
            
            // Create hardware frames context
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Console.WriteLine("[LibAvEncoder] Failed to allocate AMD D3D11VA frames context");
                CleanupHwContext();
                return false;
            }
            
            // Configure D3D11VA frames context for AMF
            AVHWFramesContext* framesCtx = (AVHWFramesContext*)_hwFramesCtx->data;
            framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            framesCtx->width = _width;
            framesCtx->height = _height;
            framesCtx->initial_pool_size = 4; // Smaller pool to reduce memory pressure
            
            Console.WriteLine($"[LibAvEncoder] Configuring D3D11VA frames: {_width}x{_height}, pool_size=4");
            
            // Configure D3D11VA-specific texture options
            AVD3D11VAFramesContext* d3d11vaFramesCtx = (AVD3D11VAFramesContext*)framesCtx->hwctx;
            if (d3d11vaFramesCtx != null)
            {
                // Set bind flags that AMF encoder expects
                // AMF needs textures with BIND_DECODER and/or BIND_SHADER_RESOURCE
                // Explicitly set RenderTarget | ShaderResource (0x28) to satisfy FFmpeg and AMF
                d3d11vaFramesCtx->BindFlags = (uint)(Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource);
                d3d11vaFramesCtx->MiscFlags = 0;
                Console.WriteLine($"[LibAvEncoder] D3D11VA frames context configured with BindFlags=0x{d3d11vaFramesCtx->BindFlags:X}");
            }
            
            ret = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
            if (ret < 0)
            {
                Console.WriteLine($"[LibAvEncoder] Failed to init AMD D3D11VA frames context with shared device: {GetErrorMessage(ret)}");
                CleanupHwContext();
                return TryInitializeAMFD3D11WithNewDevice();
            }
            
            // Set encoder to use D3D11VA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            
            Console.WriteLine("[LibAvEncoder] AMD AMF D3D11VA initialized with SHARED device (TRUE ZERO-COPY enabled!)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoder] AMD D3D11VA shared device init exception: {ex.Message}");
            CleanupHwContext();
            return TryInitializeAMFD3D11WithNewDevice();
        }
    }
    
    /// <summary>
    /// Fallback: Initialize D3D11VA with FFmpeg-created device (not zero-copy)
    /// </summary>
    private bool TryInitializeAMFD3D11WithNewDevice()
    {
        try
        {
            Console.WriteLine("[LibAvEncoder] Trying AMD AMF D3D11 with FFmpeg-created device (fallback)...");
            
            // Create D3D11VA hardware device context - let FFmpeg create its own device
            AVBufferRef* hwDeviceCtx = null;
            int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
            if (ret < 0)
            {
                Console.WriteLine($"[LibAvEncoder] AMD D3D11VA device not available: {GetErrorMessage(ret)}");
                return false;
            }
            _hwDeviceCtx = hwDeviceCtx;
            
            // Create hardware frames context
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Console.WriteLine("[LibAvEncoder] Failed to allocate AMD D3D11VA frames context");
                CleanupHwContext();
                return false;
            }
            
            // Configure D3D11VA frames context for AMF
            AVHWFramesContext* framesCtx = (AVHWFramesContext*)_hwFramesCtx->data;
            framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            framesCtx->width = _width;
            framesCtx->height = _height;
            framesCtx->initial_pool_size = 4;
            
            framesCtx->initial_pool_size = 8; // Increase pool size
            
            // Explicitly set bind flags
            AVD3D11VAFramesContext* d3d11vaFramesCtx = (AVD3D11VAFramesContext*)framesCtx->hwctx;
            if (d3d11vaFramesCtx != null)
            {
                d3d11vaFramesCtx->BindFlags = (uint)(Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource);
                d3d11vaFramesCtx->MiscFlags = 0;
            }

            ret = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
            if (ret < 0)
            {
                Console.WriteLine($"[LibAvEncoder] Failed to init AMD D3D11VA frames context: {GetErrorMessage(ret)}");
                CleanupHwContext();
                return false;
            }
            
            // Set encoder to use D3D11VA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            
            Console.WriteLine("[LibAvEncoder] AMD AMF D3D11VA initialized with FFmpeg device (hardware frames enabled)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoder] AMD D3D11VA init exception: {ex.Message}");
            CleanupHwContext();
            return false;
        }
    }
    
    private bool TryInitializeD3D11VA()
    {
        try
        {
            // Try to use shared device first
            if (_device != null && _device.NativePointer != IntPtr.Zero)
            {
                Console.WriteLine($"[LibAvEncoder] Using shared D3D11 device for Generic D3D11VA: 0x{_device.NativePointer:X}");
                
                AVBufferRef* hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                if (hwDeviceCtx != null)
                {
                    AVHWDeviceContext* deviceContext = (AVHWDeviceContext*)hwDeviceCtx->data;
                    AVD3D11VADeviceContext* d3d11vaDeviceCtx = (AVD3D11VADeviceContext*)deviceContext->hwctx;
                    d3d11vaDeviceCtx->device = (FFmpegD3D11Device*)_device.NativePointer;
                    
                    int initRet = ffmpeg.av_hwdevice_ctx_init(hwDeviceCtx);
                    if (initRet >= 0)
                    {
                        _hwDeviceCtx = hwDeviceCtx;
                        Console.WriteLine("[LibAvEncoder] Generic D3D11VA device context initialized with SHARED device");
                    }
                    else
                    {
                        Console.WriteLine($"[LibAvEncoder] Shared device init failed: {GetErrorMessage(initRet)}");
                        ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    }
                }
            }
            
            if (_hwDeviceCtx == null)
            {
                // Create D3D11VA hardware device context (New Device)
                AVBufferRef* hwDeviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (ret < 0)
                {
                    Console.WriteLine($"[LibAvEncoder] D3D11VA device not available: {GetErrorMessage(ret)}");
                    return false;
                }
                _hwDeviceCtx = hwDeviceCtx;
            }
            
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
            // framesCtx->initial_pool_size = 4; // Moved below
            
            framesCtx->initial_pool_size = 8;
            
             // Explicitly set bind flags
            AVD3D11VAFramesContext* d3d11vaFramesCtx = (AVD3D11VAFramesContext*)framesCtx->hwctx;
            if (d3d11vaFramesCtx != null)
            {
                d3d11vaFramesCtx->BindFlags = (uint)(Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource);
                d3d11vaFramesCtx->MiscFlags = 0;
            }

            int ret2 = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
            if (ret2 < 0)
            {
                Console.WriteLine($"[LibAvEncoder] Failed to init D3D11VA frames context: {GetErrorMessage(ret2)}");
                CleanupHwContext();
                return false;
            }
            
            // Set encoder to use D3D11VA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            
            // Store the D3D11 device context
            AVHWDeviceContext* devCtx = (AVHWDeviceContext*)_hwDeviceCtx->data;
            AVD3D11VADeviceContext* d3d11vaDevCtx = (AVD3D11VADeviceContext*)devCtx->hwctx;
            _ffmpegD3D11Device = d3d11vaDevCtx->device;
            _ffmpegD3D11Context = d3d11vaDevCtx->device_context;
            
            Console.WriteLine("[LibAvEncoder] D3D11VA hardware context initialized (zero-copy enabled)");
            _isD3D11VAMode = true;
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
        if (_qsvFramesCtx != null)
        {
            AVBufferRef* temp = _qsvFramesCtx;
            ffmpeg.av_buffer_unref(&temp);
            _qsvFramesCtx = null;
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
    /// TRUE ZERO-COPY: Encode D3D11 NV12 texture directly without CPU roundtrip.
    /// Only works when SupportsZeroCopyTexture is true (D3D11VA mode).
    /// Uses the capture device's D3D11 context to copy texture to FFmpeg's hardware frame.
    /// </summary>
    public bool EncodeD3D11TextureZeroCopy(D3D11Texture2D nv12Texture)
    {
        if (!_initialized || _disposed || !_isD3D11VAMode || !_useHardwareFrames || _context == null) 
            return false;
        
        lock (_lock)
        {
            try
            {
                // Get a fresh hardware frame from the pool
                int ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
                if (ret < 0)
                {
                    // If get_buffer failed, we don't hold a ref, so nothing to unref from get_buffer.
                    // But we should ensure _hwFrame is clean.
                    Console.WriteLine($"[LibAvEncoder] Failed to get hw frame buffer: {GetErrorMessage(ret)}");
                    return false;
                }
                // Console.WriteLine("[LibAvEncoder] Got hw frame buffer.");
                
                AVFrame* d3d11Frame = _hwFrame;
                AVFrame* mappedFrame = null;
                
                // If the frame is QSV, we must map it to D3D11 to get the texture pointer
                // NOTE: With our new strategy (using D3D11 frames for hw_frames_ctx), this shouldn't happen often
                // unless we fell back to direct QSV alloc.
                if (_hwFrame->format == (int)AVPixelFormat.AV_PIX_FMT_QSV)
                {
                     mappedFrame = ffmpeg.av_frame_alloc();
                     // Map QSV frame to D3D11 for WRITING (we overwrite it)
                     // AV_HWFRAME_MAP_READ(1) | AV_HWFRAME_MAP_WRITE(2) = 3
                     int mapRet = ffmpeg.av_hwframe_map(mappedFrame, _hwFrame, 3);
                     if (mapRet < 0) {
                         Console.WriteLine($"[LibAvEncoder] Map QSV to D3D11 failed: {GetErrorMessage(mapRet)}");
                         ffmpeg.av_frame_free(&mappedFrame);
                         return false;
                     }
                     d3d11Frame = mappedFrame; // Operation target is the mapped D3D11 frame
                }

                // In D3D11VA mode, data[0] is ID3D11Texture2D*
                IntPtr hwTexPtr = (IntPtr)d3d11Frame->data[0];
                int arrayIndex = (int)d3d11Frame->data[1];
                
                if (hwTexPtr == IntPtr.Zero)
                {
                    Console.WriteLine($"[LibAvEncoder] Hardware frame texture is null (Format: {d3d11Frame->format})");
                    if (mappedFrame != null) ffmpeg.av_frame_free(&mappedFrame);
                    return false;
                }
                
                // Wrap FFmpeg's D3D11 texture as Vortice texture for CopySubresourceRegion
                // IMPORTANT: The wrapper (D3D11Texture2D) calls Release() on Dispose.
                // We must increment the reference count here so that Dispose doesn't destroy the texture
                // while FFmpeg still holds a reference to it.
                Marshal.AddRef(hwTexPtr);
                using var hwTexture = new D3D11Texture2D(hwTexPtr);
                
                if (_usingCrossDeviceBridge && _encoderD3D11Device != null && _encoderD3D11Context != null)
                {
                    // --- CROSS-DEVICE BRIDGE ---
                    // 1. Ensure Shared Bridge Texture exists on Capture Device
                    if (_sharedBridgeTexture == null || _sharedBridgeTexture.Description.Width != _width || _sharedBridgeTexture.Description.Height != _height)
                    {
                        Console.WriteLine("[LibAvEncoder] Creating Shared Bridge Texture...");
                        _sharedBridgeTexture?.Dispose();
                        _sharedBridgeTexture = _device!.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)_width,
                            Height = (uint)_height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Vortice.DXGI.Format.NV12,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Default,
                            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                            CPUAccessFlags = CpuAccessFlags.None,
                            MiscFlags = ResourceOptionFlags.Shared // KEY: Allow sharing
                        });
                        
                        // Get Shared Handle
                        using var resource = _sharedBridgeTexture.QueryInterface<IDXGIResource>();
                        _lastSharedHandle = resource.SharedHandle;
                        
                        // Force re-import on next step
                        _importedBridgeTexture?.Dispose();
                        _importedBridgeTexture = null;
                    }
                    
                    // 2. Copy Input Texture -> Shared Bridge Texture (On Capture Device)
                    _context.CopyResource(_sharedBridgeTexture, nv12Texture);
                    _context.Flush(); // Ensure copy is committed
                    
                    // 3. Ensure Imported Texture exists on Encoder Device
                    if (_importedBridgeTexture == null)
                    {
                        Console.WriteLine($"[LibAvEncoder] Opening Shared Resource on Encoder Device (Handle: 0x{_lastSharedHandle:X})...");
                        _importedBridgeTexture = _encoderD3D11Device.OpenSharedResource<D3D11Texture2D>(_lastSharedHandle);
                    }
                    
                    // 4. Copy Imported Texture -> HW Frame Texture (On Encoder Device)
                    _encoderD3D11Context.CopyResource(hwTexture, _importedBridgeTexture);
                    _encoderD3D11Context.Flush();
                }
                else
                {
                    // --- SAME-DEVICE COPY ---
                    // Copy our NV12 texture to the hardware frame's texture
                    // For texture arrays, copy to the specific array index
                    _context.CopySubresourceRegion(
                        hwTexture, 
                        (uint)arrayIndex,  // DstSubresource (array index)
                        0, 0, 0,           // DstX, DstY, DstZ
                        nv12Texture, 
                        0                  // SrcSubresource
                    );
                }

                // Set PTS on the D3D11 frame (source)
                _hwFrame->pts = _frameCount++;
                
                // If we have a separate QSV Frames Context (Independent Mode), we must Transfer D3D11 -> QSV
                if (_qsvFramesCtx != null)
                {
                    AVFrame* qsvFrame = ffmpeg.av_frame_alloc();
                    // Set context so we get a frame from the QSV pool
                    int getBufRet = ffmpeg.av_hwframe_get_buffer(_qsvFramesCtx, qsvFrame, 0);
                    if (getBufRet < 0) 
                    {
                         Console.WriteLine($"[LibAvEncoder] QSV get_buffer failed: {GetErrorMessage(getBufRet)}");
                         ffmpeg.av_frame_free(&qsvFrame);
                         return false;
                    }
                    
                    qsvFrame->pts = _hwFrame->pts;

                    // 1. Try HW Transfer D3D11 -> QSV
                    int transferRet = ffmpeg.av_hwframe_transfer_data(qsvFrame, _hwFrame, 0);
                    if (transferRet < 0)
                    {
                        // 2. HW Transfer Failed: Use SW Fallback (Readback -> Upload)
                        // This handles the "Function not implemented" / "Invalid argument" errors.
                        // Log this occurrence as it affects performance significantly
                        Console.WriteLine($"[LibAvEncoder] Transfer D3D11 to QSV failed: {GetErrorMessage(transferRet)}. Using SW Fallback (Slow)...");
                        
                        ffmpeg.av_frame_unref(qsvFrame); // Clear failed content
                        ffmpeg.av_hwframe_get_buffer(_qsvFramesCtx, qsvFrame, 0); // Re-get clean buffer
                        
                        if (_stagingTexture == null) CreateStagingTexture();
                        
                        // A. Copy input texture (HW) to Staging Texture
                        _context!.CopyResource(_stagingTexture!, nv12Texture);
                        
                        // B. Map Staging Texture (Read to CPU)
                        var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        
                        AVFrame* swFrame = null;
                        try
                        {
                            // C. Create Temp SW Frame
                            swFrame = ffmpeg.av_frame_alloc();
                            swFrame->format = (int)FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NV12;
                            swFrame->width = _width;
                            swFrame->height = _height;
                            ffmpeg.av_frame_get_buffer(swFrame, 32);
                            
                            byte* src = (byte*)mapped.DataPointer;
                            int srcPitch = (int)mapped.RowPitch;
                            
                            // D. Copy Memory (CPU -> CPU)
                            // Y Plane
                            for(int y=0; y<_height; y++) 
                                Buffer.MemoryCopy(src + y*srcPitch, swFrame->data[0] + y*swFrame->linesize[0], (long)_width, (long)_width);
                                
                            // UV Plane
                            byte* srcUV = src + srcPitch*_height;
                            for(int y=0; y<_height/2; y++) 
                                Buffer.MemoryCopy(srcUV + y*srcPitch, swFrame->data[1] + y*swFrame->linesize[1], (long)_width, (long)_width);
                            
                            // E. Upload SW Frame -> QSV Frame (CPU -> VRAM)
                            transferRet = ffmpeg.av_hwframe_transfer_data(qsvFrame, swFrame, 0);
                            if (transferRet < 0) {
                                 Console.WriteLine($"[LibAvEncoder] SW->QSV upload failed: {GetErrorMessage(transferRet)}");
                                 ffmpeg.av_frame_free(&qsvFrame);
                                 return false;
                            }
                            
                            qsvFrame->pts = _hwFrame->pts;
                        }
                        finally 
                        { 
                            if(swFrame != null) ffmpeg.av_frame_free(&swFrame);
                            _context.Unmap(_stagingTexture, 0); 
                        }
                    }
                    
                    // Send QSV Frame
                    ret = ffmpeg.avcodec_send_frame(_codecCtx, qsvFrame);
                    ffmpeg.av_frame_free(&qsvFrame);
                }
                else
                {
                    // Send frame to encoder (D3D11 frame -> HW Encoder)
                    // Zero-Copy path for NVENC/AMF or QSV-Derived
                    ret = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
                }

                if (ret < 0)
                {
                    Console.WriteLine($"[LibAvEncoder] Send frame error: {GetErrorMessage(ret)}");
                    return false;
                }
                
                // Receive encoded packets
                while (true)
                {
                    // Label for loop matching not needed as we replaced block
                    // Just normal receive loop
                    ret = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;
                    if (ret < 0)
                    {
                        Console.WriteLine($"[LibAvEncoder] Receive packet error: {GetErrorMessage(ret)}");
                        break;
                    }
                    
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
                Console.WriteLine($"[LibAvEncoder] ZeroCopy encode exception: {ex.Message}");
                return false;
            }
            finally
            {
                // CRITICAL: Always release the frame reference back to the pool
                // Whether we succeeded, failed, or threw exception.
                ffmpeg.av_frame_unref(_hwFrame);
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

            if (_qsvFramesCtx != null)
            {
                fixed (AVBufferRef** b = &_qsvFramesCtx)
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
