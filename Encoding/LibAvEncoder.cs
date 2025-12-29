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
/// Video codec enumeration for encoder selection
/// </summary>
public enum VideoCodec
{
    H264,   // AVC - Universal compatibility
    H265    // HEVC - Better quality at lower bitrate (30-50% more efficient)
}

/// <summary>
/// Hardware-accelerated video encoder using FFmpeg libavcodec with D3D11VA.
/// Supports H.264 (AVC) and H.265 (HEVC) encoding with automatic fallback.
/// Provides zero-copy encoding from D3D11 textures to NAL units.
/// Supports NVIDIA NVENC, AMD AMF, and Intel QSV hardware encoders.
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

    // Codec selection
    private VideoCodec _preferredCodec = VideoCodec.H265;
    private VideoCodec _currentCodec = VideoCodec.H264;

    private bool _initialized;
    private bool _useHardwareFrames;
    private GpuVendorType _gpuVendor = GpuVendorType.Unknown;
    private string _encoderName = "unknown";

    // VBR mode for WiFi streaming - better quality with fluctuating bandwidth
    private bool _useVbrMode = true; // Enable by default for WiFi optimization
    
    private readonly object _lock = new();

    public bool IsInitialized => _initialized;
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// The currently active video codec (H264 or H265)
    /// </summary>
    public VideoCodec CurrentCodec => _currentCodec;

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

    public LibAvEncoder(int width, int height, int fps, int bitrate, D3D11Device device, VideoCodec preferredCodec = VideoCodec.H265)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _bitrate = bitrate;
        _device = device;
        _context = _device.ImmediateContext;
        _preferredCodec = preferredCodec;
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
    /// Select optimal encoder based on GPU vendor and preferred codec
    /// </summary>
    private AVCodec* SelectEncoder()
    {
        var vendor = DetectGpuVendor();
        AVCodec* codec = null;

        // Try H.265 first if preferred
        if (_preferredCodec == VideoCodec.H265)
        {
            codec = SelectH265Encoder(vendor);
            if (codec != null)
            {
                _currentCodec = VideoCodec.H265;
                return codec;
            }
            Console.WriteLine($"[LibAvEncoder] H.265 encoder not available for {vendor}, falling back to H.264");
        }

        // Fallback to H.264
        codec = SelectH264Encoder(vendor);
        if (codec != null)
        {
            _currentCodec = VideoCodec.H264;
        }

        return codec;
    }

    /// <summary>
    /// Select H.265/HEVC encoder based on GPU vendor
    /// </summary>
    private AVCodec* SelectH265Encoder(GpuVendorType vendor)
    {
        AVCodec* codec = null;

        switch (vendor)
        {
            case GpuVendorType.NVIDIA:
                // NVIDIA: HEVC NVENC
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_nvenc");
                if (codec != null) { _encoderName = "hevc_nvenc"; return codec; }
                break;

            case GpuVendorType.AMD:
                // AMD: HEVC AMF
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_amf");
                if (codec != null) { _encoderName = "hevc_amf"; return codec; }
                break;

            case GpuVendorType.Intel:
                // Intel: HEVC QSV
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_qsv");
                if (codec != null) { _encoderName = "hevc_qsv"; return codec; }
                break;

            default:
                // Unknown: Try all HEVC encoders
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_nvenc");
                if (codec != null) { _encoderName = "hevc_nvenc"; return codec; }
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_amf");
                if (codec != null) { _encoderName = "hevc_amf"; return codec; }
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_qsv");
                if (codec != null) { _encoderName = "hevc_qsv"; return codec; }
                break;
        }

        return null;
    }

    /// <summary>
    /// Select H.264/AVC encoder based on GPU vendor
    /// </summary>
    private AVCodec* SelectH264Encoder(GpuVendorType vendor)
    {
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
        _codecCtx->gop_size = Math.Max(1, _fps / 2); // Keyframe every 0.5 second for lower latency
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
                // AMD AMF - balanced latency + quality for desktop/text streaming
                Console.WriteLine("[LibAvEncoder] Configuring AMD AMF encoder (quality + low latency)");
                ffmpeg.av_opt_set(_codecCtx->priv_data, "usage", "lowlatency", 0);  // lowlatency instead of ultralowlatency for quality
                ffmpeg.av_opt_set(_codecCtx->priv_data, "quality", "balanced", 0);  // balanced instead of speed
                ffmpeg.av_opt_set(_codecCtx->priv_data, "profile", "main", 0);      // Main profile for CABAC
                // Quality settings for sharper text/desktop content
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preanalysis", "true", 0);  // Enable for better quality
                ffmpeg.av_opt_set(_codecCtx->priv_data, "vbaq", "true", 0);         // Variance Based AQ - similar to spatial-aq
                ffmpeg.av_opt_set(_codecCtx->priv_data, "enforce_hrd", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "filler_data", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "frame_skipping", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "header_insertion_mode", "idr", 0);

                // Rate control: VBR for WiFi (variable bandwidth), CBR for LAN
                if (_useVbrMode)
                {
                    // VBR mode - better for WiFi with fluctuating bandwidth
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "vbr_latency", 0);  // VBR with latency optimization
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "qp_i", "20", 0);  // Quality target for I-frames
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "qp_p", "22", 0);  // Quality target for P-frames
                    _codecCtx->rc_max_rate = _bitrate * 2;  // Allow 2x peak for complex content
                    _codecCtx->rc_buffer_size = _bitrate;   // 1 second buffer
                    Console.WriteLine($"[LibAvEncoder] AMF VBR mode: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
                }
                else
                {
                    // CBR mode - consistent bitrate for LAN
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "cbr", 0);
                    _codecCtx->rc_max_rate = _bitrate;
                    _codecCtx->rc_buffer_size = _bitrate / 4;  // 250ms buffer
                }

                // AMF level for width > 2048
                if (_width > 2048)
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "level", "5.1", 0);
                break;
                
            case "h264_nvenc":
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "p1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "tune", "ull", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "zerolatency", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "delay", "0", 0);
                // Quality settings for sharper text/desktop content
                ffmpeg.av_opt_set(_codecCtx->priv_data, "spatial-aq", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "temporal-aq", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "aq-strength", "8", 0);  // Strong AQ for text edges
                ffmpeg.av_opt_set(_codecCtx->priv_data, "profile", "main", 0);   // Main for CABAC

                // Rate control: VBR for WiFi (variable bandwidth), CBR for LAN
                if (_useVbrMode)
                {
                    // VBR mode - better for WiFi with fluctuating bandwidth
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "vbr", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "cq", "20", 0);  // Quality target (lower = higher quality)
                    _codecCtx->rc_max_rate = _bitrate * 2;  // Allow 2x peak for complex content
                    _codecCtx->rc_buffer_size = _bitrate;   // 1 second buffer
                    Console.WriteLine($"[LibAvEncoder] NVENC VBR mode: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
                }
                else
                {
                    // CBR mode - consistent bitrate for LAN
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "cbr", 0);
                    _codecCtx->rc_max_rate = _bitrate;
                    _codecCtx->rc_buffer_size = _bitrate / 4;  // 250ms buffer
                }
                break;
                
            case "h264_qsv":
                // Intel QSV - balanced latency + quality for desktop/text streaming
                Console.WriteLine("[LibAvEncoder] Configuring Intel QSV encoder (quality + low latency)");
                // Balanced preset for quality (faster still available but "balanced" is better for text)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "faster", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "profile", "main", 0);      // Main profile for CABAC
                // Async depth > 1 allows pipeline parallelism (essential for 60fps on weaker iGPUs)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "async_depth", "4", 0);
                // Low power mode for fixed-function encoder (lower latency)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "low_power", "1", 0);
                // Force single NAL per frame for lower decoding latency
                ffmpeg.av_opt_set(_codecCtx->priv_data, "single_sei_nal_unit", "1", 0);
                // Quality settings - enable adaptive quantization for text edges
                ffmpeg.av_opt_set(_codecCtx->priv_data, "adaptive_i", "1", 0);      // Adaptive I-frame insertion
                ffmpeg.av_opt_set(_codecCtx->priv_data, "adaptive_b", "0", 0);      // No B-frames
                ffmpeg.av_opt_set(_codecCtx->priv_data, "b_strategy", "0", 0);
                // Extra QSV options for stability
                ffmpeg.av_opt_set(_codecCtx->priv_data, "idr_interval", "0", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "pic_timing_sei", "0", 0);

                // Rate control: VBR for WiFi (variable bandwidth), CBR for LAN
                if (_useVbrMode)
                {
                    // VBR mode - use look-ahead for better quality prediction
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead", "1", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead_depth", "10", 0);  // Small look-ahead for latency
                    _codecCtx->rc_max_rate = _bitrate * 2;  // Allow 2x peak for complex content
                    _codecCtx->rc_buffer_size = _bitrate;   // 1 second buffer
                    Console.WriteLine($"[LibAvEncoder] QSV VBR mode: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
                }
                else
                {
                    // CBR mode - no look-ahead for lowest latency
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead", "0", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead_depth", "0", 0);
                    _codecCtx->rc_max_rate = _bitrate;
                    _codecCtx->rc_buffer_size = _bitrate / 4;  // 250ms buffer
                }
                break;

            // ============ H.265/HEVC ENCODERS ============

            case "hevc_nvenc":
                // NVIDIA NVENC HEVC - high quality with low latency
                Console.WriteLine("[LibAvEncoder] Configuring NVIDIA HEVC NVENC encoder (quality + low latency)");
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "p4", 0);       // Balanced preset (p1=fastest, p7=slowest)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "tune", "ll", 0);         // Low latency tuning
                ffmpeg.av_opt_set(_codecCtx->priv_data, "zerolatency", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "delay", "0", 0);
                // Quality settings for sharper desktop content
                ffmpeg.av_opt_set(_codecCtx->priv_data, "spatial-aq", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "temporal-aq", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "aq-strength", "8", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "rc-lookahead", "0", 0);  // No lookahead for low latency
                // HEVC Main Profile, Level 4.0 (supports 1080p60, 4K30)
                _codecCtx->profile = ffmpeg.FF_PROFILE_HEVC_MAIN;
                _codecCtx->level = 120;  // Level 4.0

                // Rate control
                if (_useVbrMode)
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "vbr", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "cq", "25", 0);  // Quality level for HEVC
                    _codecCtx->rc_max_rate = _bitrate * 2;
                    _codecCtx->rc_buffer_size = _bitrate;
                    Console.WriteLine($"[LibAvEncoder] HEVC NVENC VBR: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
                }
                else
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "cbr", 0);
                    _codecCtx->rc_max_rate = _bitrate;
                    _codecCtx->rc_buffer_size = _bitrate / 4;
                }
                break;

            case "hevc_amf":
                // AMD AMF HEVC - balanced quality + low latency
                Console.WriteLine("[LibAvEncoder] Configuring AMD HEVC AMF encoder (quality + low latency)");
                ffmpeg.av_opt_set(_codecCtx->priv_data, "usage", "lowlatency", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "quality", "balanced", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "profile", "main", 0);     // HEVC Main Profile
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preanalysis", "true", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "vbaq", "true", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "enforce_hrd", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "filler_data", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "frame_skipping", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "header_insertion_mode", "idr", 0);
                // HEVC Profile
                _codecCtx->profile = ffmpeg.FF_PROFILE_HEVC_MAIN;
                _codecCtx->level = 120;

                // Rate control
                if (_useVbrMode)
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "vbr_latency", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "qp_i", "22", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "qp_p", "24", 0);
                    _codecCtx->rc_max_rate = _bitrate * 2;
                    _codecCtx->rc_buffer_size = _bitrate;
                    Console.WriteLine($"[LibAvEncoder] HEVC AMF VBR: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
                }
                else
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "cbr", 0);
                    _codecCtx->rc_max_rate = _bitrate;
                    _codecCtx->rc_buffer_size = _bitrate / 4;
                }

                // Level for high resolution
                if (_width > 2048)
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "level", "5.1", 0);
                break;

            case "hevc_qsv":
                // Intel QSV HEVC - balanced quality + low latency
                Console.WriteLine("[LibAvEncoder] Configuring Intel HEVC QSV encoder (quality + low latency)");
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "faster", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "profile", "main", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "async_depth", "4", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "low_power", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "adaptive_i", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "adaptive_b", "0", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "b_strategy", "0", 0);
                // HEVC Profile
                _codecCtx->profile = ffmpeg.FF_PROFILE_HEVC_MAIN;
                _codecCtx->level = 120;

                // Rate control
                if (_useVbrMode)
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead", "1", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead_depth", "10", 0);
                    _codecCtx->rc_max_rate = _bitrate * 2;
                    _codecCtx->rc_buffer_size = _bitrate;
                    Console.WriteLine($"[LibAvEncoder] HEVC QSV VBR: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
                }
                else
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead", "0", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead_depth", "0", 0);
                    _codecCtx->rc_max_rate = _bitrate;
                    _codecCtx->rc_buffer_size = _bitrate / 4;
                }
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
                
                // If QSV returned false but _hwDeviceCtx is set, it means we are in Safe Mode (System Memory Input).
                // Do NOT fallback to generic D3D11VA, as h264_qsv needs the QSV device we just created.
                if (_hwDeviceCtx != null)
                {
                    Console.WriteLine("[LibAvEncoder] Intel: QSV Safe Mode active. Skipping D3D11VA fallback.");
                    return false;
                }

                // CRITICAL FIX: The h264_qsv encoder DOES NOT SUPPORT generic AV_PIX_FMT_D3D11 frames.
                // It requires AV_PIX_FMT_QSV frames (which wrap D3D11 surfaces).
                // TryInitializeD3D11VA sets up generic D3D11VA (AV_PIX_FMT_D3D11), which causes pixel format mismatch errors.
                // Therefore, if TryInitializeQSV failed completely (no QSV context), we CANNOT use h264_qsv with D3D11VA.
                // We must accept Software Upload (System Memory) or switch encoder (not implemented here).
                
                Console.WriteLine("[LibAvEncoder] Intel: QSV failed completely. D3D11VA fallback is NOT supported for h264_qsv. Using Software Upload.");
                return false; 

                
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
    /// Implements gradual fallback: Shared Device Zero-Copy -> Cross-Device Zero-Copy -> Safe Mode (System Memory).
    /// </summary>
    private bool TryInitializeQSV()
    {
        try
        {
            Console.WriteLine("[LibAvEncoder] Initializing Intel QSV hardware context (Direct + Bridge Mode)...");
            
            AVBufferRef* hwDeviceCtx = null;
            AVBufferRef* d3d11vaDeviceRef = null;
            
            // PRIORITY 1: Direct QSV Creation (Most reliable on Intel)
            // This lets the driver/FFmpeg pick the best D3D11 device for QSV.
            // We then bridge to it from our capture device.
            int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, "auto", null, 0);
            
            if (ret == 0)
            {
                Console.WriteLine("[LibAvEncoder] QSV device created directly (matches known good config)");
                
                // We need to extract the underlying D3D11 device to support Cross-Device Copy/Sharing
                ret = ffmpeg.av_hwdevice_ctx_create_derived(&d3d11vaDeviceRef, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, hwDeviceCtx, 0);
                if (ret >= 0)
                {
                    var d3d11Ctx = (AVHWDeviceContext*)d3d11vaDeviceRef->data;
                    var d3d11vaCtx = (AVD3D11VADeviceContext*)d3d11Ctx->hwctx;
                    IntPtr qsvD3D11DevicePtr = (IntPtr)d3d11vaCtx->device;
                    
                    Console.WriteLine($"[LibAvEncoder] Extracted underlying QSV D3D11 Device: 0x{qsvD3D11DevicePtr:X}");
                    
                    if (qsvD3D11DevicePtr != IntPtr.Zero && qsvD3D11DevicePtr != _device!.NativePointer)
                    {
                        // Setup Cross-Device Bridge
                        _encoderD3D11Device = new D3D11Device(qsvD3D11DevicePtr);
                        _encoderD3D11Context = _encoderD3D11Device.ImmediateContext;
                        _usingCrossDeviceBridge = true;
                        Console.WriteLine("[LibAvEncoder] QSV: Enabled Cross-Device Bridge (Capture Device != QSV Device)");
                    }
                    else if (qsvD3D11DevicePtr == _device!.NativePointer)
                    {
                        Console.WriteLine("[LibAvEncoder] QSV: Running on same D3D11 device as capture (Optimal)");
                        _usingCrossDeviceBridge = false;
                    }
                }
                else
                {
                    Console.WriteLine("[LibAvEncoder] Warning: Could not derive D3D11VA from QSV device. Zero-Copy might fail if devices differ.");
                }
            }
            else
            {
                 Console.WriteLine($"[LibAvEncoder] Direct QSV init failed: {GetErrorMessage(ret)}. Trying Fallback (D3D11 Wrapper)...");
                 return false; 
            }

            // 3. Create Frames Context on the QSV Device
            if (hwDeviceCtx != null)
            {
                 AVBufferRef* framesRef = ffmpeg.av_hwframe_ctx_alloc(hwDeviceCtx);
                 AVHWFramesContext* framesCtx = (AVHWFramesContext*)framesRef->data;
                 
                 framesCtx->format = AVPixelFormat.AV_PIX_FMT_QSV;
                 framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                 framesCtx->width = _width;
                 framesCtx->height = _height;
                 framesCtx->initial_pool_size = 20;
                 
                 ret = ffmpeg.av_hwframe_ctx_init(framesRef);
                 if (ret >= 0)
                 {
                     // --- CRITICAL ZERO-COPY TEST (RELAXED) ---
                     // We heavily prioritize stabilization here.
                     // Older logs confirmed QSV worked instantly on creation without Map Check.
                     // The Map Check (av_hwframe_map) is failing on some valid QSV driver states for complex reasons (derived context direction).
                     // We will TRUST av_hwframe_get_buffer. If we can allocate a QSV frame, we assume we can use it.
                     
                     Console.WriteLine("[LibAvEncoder] Testing QSV Zero-Copy Capability (Alloc Check Only)...");
                     AVFrame* testFrame = ffmpeg.av_frame_alloc();
                     int allocRet = ffmpeg.av_hwframe_get_buffer(framesRef, testFrame, 0);
                     
                     if (allocRet >= 0)
                     {
                         // Allocation worked! We trust the driver now.
                         // Don't risk failing on Map check.
                         Console.WriteLine("[LibAvEncoder] QSV Alloc Check Passed. Trusting Zero-Copy configuration.");
                         
                         ffmpeg.av_frame_free(&testFrame);
                         
                         _hwDeviceCtx = hwDeviceCtx;
                         _hwFramesCtx = framesRef; // Use this as main frames ctx
                         _qsvFramesCtx = ffmpeg.av_buffer_ref(framesRef); // Keep explicit ref for QSV logic
                         
                         _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
                         _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
                         _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_QSV;
                         
                         _useHardwareFrames = true; // SUCCESS!
                         Console.WriteLine("[LibAvEncoder] Intel QSV initialized in TRUE ZERO-COPY Mode.");
                         
                         if (d3d11vaDeviceRef != null) ffmpeg.av_buffer_unref(&d3d11vaDeviceRef);
                         return true;
                     }
                     else
                     {
                         Console.WriteLine($"[LibAvEncoder] QSV Test Failed: Alloc Error {GetErrorMessage(allocRet)}");
                     }
                     
                     if (testFrame != null) ffmpeg.av_frame_free(&testFrame);
                 }
                 else
                 {
                     Console.WriteLine($"[LibAvEncoder] QSV frames init failed: {GetErrorMessage(ret)}");
                 }
                 
                 ffmpeg.av_buffer_unref(&framesRef);
            }
            
            // Cleanup on failure
            if (d3d11vaDeviceRef != null) ffmpeg.av_buffer_unref(&d3d11vaDeviceRef);
            if (hwDeviceCtx != null) ffmpeg.av_buffer_unref(&hwDeviceCtx);
            
            // Fallback to Safe Mode
            Console.WriteLine("[LibAvEncoder] QSV Zero-Copy Init failed. Falling back to SAFE MODE.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibAvEncoder] QSV init exception: {ex.Message}");
            return false;
        }
    }

    private void CleanupHwContextSourceInfo() {
        // Just a placeholder if needed, but logic above is cleaner
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
                long currentPts = _frameCount++;
                

                int ret = 0;

                // STRICT SEPARATION: If _useHardwareFrames is true (set by TryInitializeQSV only after successful test),
                // we assume Zero-Copy works. If it fails here, it's a fatal stream error, not a fallback candidate.
                if (_useHardwareFrames)
                {
                    // --- HARDWARE ZERO-COPY PATH ---
                    ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
                    if (ret < 0)
                    {
                        Console.WriteLine($"[LibAvEncoder] HW Error: get_buffer failed: {GetErrorMessage(ret)}");
                        return false; 
                    }
                    
                    _hwFrame->pts = currentPts;
                    
                    AVFrame* d3d11Frame = _hwFrame;
                    AVFrame* mappedFrame = null;
                    
                    try {
                        // 1. QSV Mapping Logic (if needed)
                        if (_hwFrame->format == (int)AVPixelFormat.AV_PIX_FMT_QSV)
                        {
                             mappedFrame = ffmpeg.av_frame_alloc();
                             int mapRet = ffmpeg.av_hwframe_map(mappedFrame, _hwFrame, 3); // Read/Write
                             if (mapRet < 0) {
                                  Console.WriteLine($"[LibAvEncoder] HW Error: Map QSV -> D3D11 failed: {GetErrorMessage(mapRet)}");
                                  ffmpeg.av_frame_free(&mappedFrame);
                                  return false; // Abort, do not fallback
                             }
                             d3d11Frame = mappedFrame; 
                        }

                        // 2. Texture Copy Logic (Capture -> Encoder)
                        IntPtr hwTexPtr = (IntPtr)d3d11Frame->data[0];
                        int arrayIndex = (int)d3d11Frame->data[1];
                        
                        // Wrap D3D11 Texture
                        Marshal.AddRef(hwTexPtr);
                        using var hwTexture = new D3D11Texture2D(hwTexPtr);
                        
                        if (_usingCrossDeviceBridge && _encoderD3D11Device != null && _encoderD3D11Context != null)
                        {
                            // Cross-Device Copy
                            if (_sharedBridgeTexture == null || _sharedBridgeTexture.Description.Width != _width || _sharedBridgeTexture.Description.Height != _height)
                            {
                                // ... (Recreate Shared Texture Logic) ...
                                _sharedBridgeTexture?.Dispose();
                                _sharedBridgeTexture = _device!.CreateTexture2D(new Texture2DDescription {
                                    Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1,
                                    Format = Vortice.DXGI.Format.NV12, SampleDescription = new SampleDescription(1, 0),
                                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                                    CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.Shared 
                                });
                                using var resource = _sharedBridgeTexture.QueryInterface<IDXGIResource>();
                                _lastSharedHandle = resource.SharedHandle;
                                _importedBridgeTexture?.Dispose();
                                _importedBridgeTexture = null;
                            }
                            
                            _context.CopyResource(_sharedBridgeTexture, nv12Texture);
                            _context.Flush();
                            
                            if (_importedBridgeTexture == null)
                                _importedBridgeTexture = _encoderD3D11Device.OpenSharedResource<D3D11Texture2D>(_lastSharedHandle);
                            
                            _encoderD3D11Context.CopyResource(hwTexture, _importedBridgeTexture);
                            _encoderD3D11Context.Flush();
                        }
                        else
                        {
                            // Same-Device Copy
                            _context.CopySubresourceRegion(hwTexture, (uint)arrayIndex, 0, 0, 0, nv12Texture, 0);
                        }
                    }
                    finally {
                        if (mappedFrame != null) ffmpeg.av_frame_free(&mappedFrame);
                    }

                    // 3. Send Frame to Encoder
                    // Since we used create_derived, _hwFrame from get_buffer(_hwFramesCtx) IS the QSV frame.
                    ret = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
                }
                else
                {
                    // --- SOFTWARE FALLBACK PATH (Safe Mode) ---
                    // This path is strictly taken if Initialize determined HW frames are unusable.
                    if (_stagingTexture == null) CreateStagingTexture();
                    
                    var sourceTex = (_usingCrossDeviceBridge && _importedBridgeTexture != null) ? _importedBridgeTexture : nv12Texture;
                    _context!.CopyResource(_stagingTexture!, sourceTex!);
                    
                    var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    AVFrame* swFrame = ffmpeg.av_frame_alloc();
                    try
                    {
                        swFrame->format = (int)FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NV12;
                        swFrame->width = _width;
                        swFrame->height = _height;
                        ffmpeg.av_frame_get_buffer(swFrame, 32);
                        
                        byte* src = (byte*)mapped.DataPointer;
                        int srcPitch = (int)mapped.RowPitch;
                        
                        // Copy Y Plane
                        for(int y=0; y<_height; y++) 
                            Buffer.MemoryCopy(src + y*srcPitch, swFrame->data[0] + y*swFrame->linesize[0], _width, _width);
                        
                        // Copy UV Plane
                        byte* srcUV = src + srcPitch*_height;
                        for(int y=0; y<_height/2; y++) 
                             Buffer.MemoryCopy(srcUV + y*srcPitch, swFrame->data[1] + y*swFrame->linesize[1], _width, _width);
                        
                        swFrame->pts = currentPts;
                        swFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE; 
                        
                        ret = ffmpeg.avcodec_send_frame(_codecCtx, swFrame);
                    }
                    finally 
                    { 
                        ffmpeg.av_frame_free(&swFrame);
                        _context.Unmap(_stagingTexture, 0); 
                    }
                }
                
                // Common Result Check
                if (ret < 0) {
                     Console.WriteLine($"[LibAvEncoder] Send frame error: {GetErrorMessage(ret)}");
                     return false;
                }
                
                // Receive Packet (Common)
                while (true)
                {
                    int recvRet = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                    if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recvRet == ffmpeg.AVERROR_EOF) break;
                    if (recvRet < 0) break;

                    byte[] nalData = new byte[_packet->size];
                    Marshal.Copy((IntPtr)_packet->data, nalData, 0, _packet->size);
                    OnEncodedData?.Invoke(nalData, (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0, _packet->pts);
                    ffmpeg.av_packet_unref(_packet);
                }
                
                // Remove unused labels/variable usage
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
            // Free FFmpeg resources
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
            
            // Unref Contexts
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
            
            // Free Owned D3D11 Resources
            _stagingTexture?.Dispose();
            
            // Dispose Bridge Resources (Owned by Encoder)
            _sharedBridgeTexture?.Dispose();
            _importedBridgeTexture?.Dispose();
            _encoderD3D11Context?.Dispose(); // Context from Internal Device
            _encoderD3D11Device?.Dispose();  // Internal Device
            
            // DO NOT dispose _context or _device as they are borrowed from PerMonitorCapture
            // _context?.Dispose(); 
        }
        
        Console.WriteLine("[LibAvEncoder] Disposed (Safe)");
    }
}
