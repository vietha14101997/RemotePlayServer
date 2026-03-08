#nullable enable
using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.DXGI;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Encoding;

public unsafe partial class LibAvEncoder
{
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
                    Logger.Info($"[LibAvEncoder] Detected GPU: NVIDIA ({desc.Description})");
                    return _gpuVendor;
                }
                if (name.Contains("AMD") || name.Contains("RADEON") || name.Contains("RX "))
                {
                    _gpuVendor = GpuVendorType.AMD;
                    Logger.Info($"[LibAvEncoder] Detected GPU: AMD ({desc.Description})");
                    return _gpuVendor;
                }
                if (name.Contains("INTEL") || name.Contains("UHD") || name.Contains("IRIS"))
                {
                    _gpuVendor = GpuVendorType.Intel;
                    Logger.Info($"[LibAvEncoder] Detected GPU: Intel ({desc.Description})");
                    return _gpuVendor;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"[LibAvEncoder] GPU detection error: {ex.Message}");
        }

        Logger.Info("[LibAvEncoder] GPU vendor: Unknown");
        return GpuVendorType.Unknown;
    }

    /// <summary>
    /// Select optimal encoder based on GPU vendor and preferred codec.
    /// Fallback chain: H265 -> H264 -> VP9 -> VP8
    /// </summary>
    private AVCodec* SelectEncoder()
    {
        Logger.Info($"[LibAvEncoder] SelectEncoder: preferredCodec={_preferredCodec}");
        var vendor = DetectGpuVendor();
        Logger.Info($"[LibAvEncoder] SelectEncoder: vendor={vendor}");
        AVCodec* codec = null;

        // Try encoders based on preferred codec with fallback chain
        switch (_preferredCodec)
        {
            case VideoCodec.H265:
                Logger.Info("[LibAvEncoder] Trying H.265 encoder...");
                codec = SelectH265Encoder(vendor);
                if (codec != null)
                {
                    _currentCodec = VideoCodec.H265;
                    Logger.Info($"[LibAvEncoder] Selected H.265 encoder: {_encoderName}");
                    return codec;
                }
                Logger.Error($"[LibAvEncoder] H.265 not available, falling back to H.264...");
                goto case VideoCodec.H264;

            case VideoCodec.H264:
                Logger.Info("[LibAvEncoder] Trying H.264 encoder...");
                codec = SelectH264Encoder(vendor);
                if (codec != null)
                {
                    _currentCodec = VideoCodec.H264;
                    Logger.Info($"[LibAvEncoder] Selected H.264 encoder: {_encoderName}");
                    return codec;
                }
                Logger.Error("[LibAvEncoder] H.264 not available, falling back to VP9...");
                goto case VideoCodec.VP9;

            case VideoCodec.VP9:
                codec = SelectVP9Encoder();
                if (codec != null)
                {
                    _currentCodec = VideoCodec.VP9;
                    Logger.Info($"[LibAvEncoder] Selected VP9 encoder: {_encoderName}");
                    return codec;
                }
                Logger.Error("[LibAvEncoder] VP9 not available, falling back to VP8...");
                goto case VideoCodec.VP8;

            case VideoCodec.VP8:
                codec = SelectVP8Encoder();
                if (codec != null)
                {
                    _currentCodec = VideoCodec.VP8;
                    Logger.Info($"[LibAvEncoder] Selected VP8 encoder: {_encoderName}");
                    return codec;
                }
                break;
        }

        Logger.Error("[LibAvEncoder] ERROR: No encoder found! All fallbacks failed.");
        return null;
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
        Logger.Info($"[LibAvEncoder] SelectH264Encoder: vendor={vendor}");

        // Helper to safely try finding encoder
        AVCodec* TryFindEncoder(string name)
        {
            try
            {
                Logger.Info($"[LibAvEncoder] Trying encoder: {name}");
                var c = ffmpeg.avcodec_find_encoder_by_name(name);
                Logger.Error($"[LibAvEncoder] Encoder {name}: {(c != null ? "FOUND" : "not found")}");
                return c;
            }
            catch (Exception ex)
            {
                Logger.Info($"[LibAvEncoder] Error finding encoder {name}: {ex.Message}");
                return null;
            }
        }

        switch (vendor)
        {
            case GpuVendorType.AMD:
                // AMD: Prefer AMF encoder
                codec = TryFindEncoder("h264_amf");
                if (codec != null) { _encoderName = "h264_amf"; return codec; }
                codec = TryFindEncoder("h264_qsv");
                if (codec != null) { _encoderName = "h264_qsv"; return codec; }
                break;

            case GpuVendorType.NVIDIA:
                // NVIDIA: Prefer NVENC encoder
                codec = TryFindEncoder("h264_nvenc");
                if (codec != null) { _encoderName = "h264_nvenc"; return codec; }
                codec = TryFindEncoder("h264_qsv");
                if (codec != null) { _encoderName = "h264_qsv"; return codec; }
                break;

            case GpuVendorType.Intel:
                // Intel: Prefer QSV encoder
                codec = TryFindEncoder("h264_qsv");
                if (codec != null) { _encoderName = "h264_qsv"; return codec; }
                codec = TryFindEncoder("h264_nvenc");
                if (codec != null) { _encoderName = "h264_nvenc"; return codec; }
                break;

            default:
                // Unknown: Try all in order
                codec = TryFindEncoder("h264_nvenc");
                if (codec != null) { _encoderName = "h264_nvenc"; return codec; }
                codec = TryFindEncoder("h264_amf");
                if (codec != null) { _encoderName = "h264_amf"; return codec; }
                codec = TryFindEncoder("h264_qsv");
                if (codec != null) { _encoderName = "h264_qsv"; return codec; }
                break;
        }

        return codec;
    }

    /// <summary>
    /// Select VP9 encoder (libvpx-vp9 software encoder)
    /// </summary>
    private AVCodec* SelectVP9Encoder()
    {
        Logger.Info("[LibAvEncoder] Trying VP9 encoder (libvpx-vp9)...");
        var codec = ffmpeg.avcodec_find_encoder_by_name("libvpx-vp9");
        if (codec != null)
        {
            _encoderName = "libvpx-vp9";
            Logger.Info("[LibAvEncoder] VP9 encoder found: libvpx-vp9");
            return codec;
        }
        Logger.Error("[LibAvEncoder] VP9 encoder not found");
        return null;
    }

    /// <summary>
    /// Select VP8 encoder (libvpx software encoder)
    /// </summary>
    private AVCodec* SelectVP8Encoder()
    {
        Logger.Info("[LibAvEncoder] Trying VP8 encoder (libvpx)...");
        var codec = ffmpeg.avcodec_find_encoder_by_name("libvpx");
        if (codec != null)
        {
            _encoderName = "libvpx";
            Logger.Info("[LibAvEncoder] VP8 encoder found: libvpx");
            return codec;
        }
        Logger.Error("[LibAvEncoder] VP8 encoder not found");
        return null;
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
        // 1s GOP - periodic I-frames for WiFi resilience
        // Short enough to recover quickly from packet loss, long enough to avoid bandwidth waste
        _codecCtx->gop_size = _fps;  // 1 second GOP
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
                Logger.Info("[LibAvEncoder] Configuring AMD AMF encoder (quality + low latency)");
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
                    _codecCtx->rc_max_rate = _bitrate * 3;  // Allow 3x peak for scene change
                    _codecCtx->rc_buffer_size = _bitrate / 2;   // 500ms buffer for faster response
                    Logger.Info($"[LibAvEncoder] AMF VBR mode: target {_bitrate/1000}kbps, max {_bitrate*3/1000}kbps");
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
                ffmpeg.av_opt_set(_codecCtx->priv_data, "no_scenecut", "0", 0);  // Enable scene change detection
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
                    _codecCtx->rc_max_rate = _bitrate * 3;  // Allow 3x peak for scene change
                    _codecCtx->rc_buffer_size = _bitrate / 2;   // 500ms buffer for faster response
                    Logger.Info($"[LibAvEncoder] NVENC VBR mode: target {_bitrate/1000}kbps, max {_bitrate*3/1000}kbps");
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
                Logger.Info("[LibAvEncoder] Configuring Intel QSV encoder (quality + low latency)");
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
                    _codecCtx->rc_max_rate = _bitrate * 3;  // Allow 3x peak for scene change
                    _codecCtx->rc_buffer_size = _bitrate / 2;   // 500ms buffer for faster response
                    Logger.Info($"[LibAvEncoder] QSV VBR mode: target {_bitrate/1000}kbps, max {_bitrate*3/1000}kbps");
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
                // NVIDIA NVENC HEVC - optimized for text/desktop content sharpness
                Logger.Info("[LibAvEncoder] Configuring NVIDIA HEVC NVENC encoder (text-optimized)");
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "p4", 0);       // Balanced preset (p1=fastest, p7=slowest)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "tune", "ll", 0);         // Low latency tuning
                ffmpeg.av_opt_set(_codecCtx->priv_data, "zerolatency", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "delay", "0", 0);
                // Quality settings for sharper desktop content
                ffmpeg.av_opt_set(_codecCtx->priv_data, "spatial-aq", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "temporal-aq", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "aq-strength", "8", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "rc-lookahead", "0", 0);  // No lookahead for low latency
                ffmpeg.av_opt_set(_codecCtx->priv_data, "no-scenecut", "0", 0);   // Enable scene change detection
                // HEVC Main Profile, Level 4.0 (supports 1080p60, 4K30)
                _codecCtx->profile = ffmpeg.FF_PROFILE_HEVC_MAIN;
                _codecCtx->level = 120;  // Level 4.0

                // Rate control — conservative for DataChannel (SCTP) transport
                if (_useVbrMode)
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "vbr", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "cq", "23", 0);  // Balanced: sharper than 25 but no frame size explosion
                    _codecCtx->rc_max_rate = _bitrate * 2;       // 2x peak (conservative for SCTP)
                    _codecCtx->rc_buffer_size = _bitrate / 2;    // 500ms buffer for responsive rate control
                    Logger.Info($"[LibAvEncoder] HEVC NVENC VBR: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
                }
                else
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "cbr", 0);
                    _codecCtx->rc_max_rate = _bitrate;
                    _codecCtx->rc_buffer_size = _bitrate / 4;
                }
                break;

            case "hevc_amf":
                // AMD AMF HEVC - optimized for text/desktop content sharpness
                Logger.Info("[LibAvEncoder] Configuring AMD HEVC AMF encoder (text-optimized)");
                ffmpeg.av_opt_set(_codecCtx->priv_data, "usage", "lowlatency", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "quality", "balanced", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "profile", "main", 0);     // HEVC Main Profile
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preanalysis", "true", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "vbaq", "true", 0);        // Critical: allocates bits to text edges
                ffmpeg.av_opt_set(_codecCtx->priv_data, "enforce_hrd", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "filler_data", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "frame_skipping", "false", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "header_insertion_mode", "idr", 0);
                // HEVC Profile
                _codecCtx->profile = ffmpeg.FF_PROFILE_HEVC_MAIN;
                _codecCtx->level = 120;

                // Rate control — conservative for DataChannel (SCTP) transport
                if (_useVbrMode)
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "rc", "vbr_latency", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "qp_i", "21", 0);    // Slightly sharper than original 22
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "qp_p", "23", 0);    // Slightly sharper than original 24
                    _codecCtx->rc_max_rate = _bitrate * 2;       // 2x peak (conservative for SCTP)
                    _codecCtx->rc_buffer_size = _bitrate / 2;    // 500ms buffer for responsive rate control
                    Logger.Info($"[LibAvEncoder] HEVC AMF VBR: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
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
                // Intel QSV HEVC - optimized for text/desktop content sharpness
                Logger.Info("[LibAvEncoder] Configuring Intel HEVC QSV encoder (text-optimized)");
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

                // Rate control — conservative for DataChannel (SCTP) transport
                if (_useVbrMode)
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead", "1", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead_depth", "10", 0);
                    _codecCtx->rc_max_rate = _bitrate * 2;       // 2x peak (conservative for SCTP)
                    _codecCtx->rc_buffer_size = _bitrate / 2;    // 500ms buffer for responsive rate control
                    Logger.Info($"[LibAvEncoder] HEVC QSV VBR: target {_bitrate/1000}kbps, max {_bitrate*2/1000}kbps");
                }
                else
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead", "0", 0);
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "look_ahead_depth", "0", 0);
                    _codecCtx->rc_max_rate = _bitrate;
                    _codecCtx->rc_buffer_size = _bitrate / 4;
                }
                break;

            // ============ VP9/VP8 SOFTWARE ENCODERS (FALLBACK) ============

            case "libvpx-vp9":
                // VP9 software encoder - realtime mode for low latency streaming
                Logger.Info("[LibAvEncoder] Configuring VP9 encoder (libvpx-vp9) for realtime streaming");

                // Use YUV420P for VP9 (libvpx doesn't support NV12 directly)
                _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

                // Realtime encoding for low latency
                ffmpeg.av_opt_set(_codecCtx->priv_data, "deadline", "realtime", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "cpu-used", "8", 0);  // 0-8, higher = faster
                ffmpeg.av_opt_set(_codecCtx->priv_data, "lag-in-frames", "0", 0);  // No frame buffering
                ffmpeg.av_opt_set(_codecCtx->priv_data, "error-resilient", "1", 0);  // Error resilience for streaming
                ffmpeg.av_opt_set(_codecCtx->priv_data, "row-mt", "1", 0);  // Row-based multithreading
                ffmpeg.av_opt_set(_codecCtx->priv_data, "tile-columns", "2", 0);  // Parallel tile encoding
                ffmpeg.av_opt_set(_codecCtx->priv_data, "frame-parallel", "1", 0);

                // Use multiple threads for VP9 (software encoder benefits from threading)
                _codecCtx->thread_count = Math.Max(2, Environment.ProcessorCount / 2);

                // Bitrate settings
                _codecCtx->bit_rate = _bitrate;
                _codecCtx->rc_max_rate = _bitrate * 2;  // Allow 2x burst for scene changes
                _codecCtx->rc_buffer_size = _bitrate;   // 1 second buffer

                Logger.Info($"[LibAvEncoder] VP9 config: {_bitrate/1000}kbps, {_codecCtx->thread_count} threads, cpu-used=8");
                break;

            case "libvpx":
                // VP8 software encoder - fastest mode for final fallback
                Logger.Info("[LibAvEncoder] Configuring VP8 encoder (libvpx) for realtime streaming");

                // Use YUV420P for VP8
                _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

                // Maximum speed for low latency
                ffmpeg.av_opt_set(_codecCtx->priv_data, "deadline", "realtime", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "cpu-used", "16", 0);  // Max speed for VP8 (0-16)
                ffmpeg.av_opt_set(_codecCtx->priv_data, "lag-in-frames", "0", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "error-resilient", "1", 0);
                ffmpeg.av_opt_set(_codecCtx->priv_data, "token-parts", "2", 0);  // Parallel token encoding

                // Use multiple threads
                _codecCtx->thread_count = Math.Max(2, Environment.ProcessorCount / 2);

                // Bitrate settings
                _codecCtx->bit_rate = _bitrate;
                _codecCtx->rc_max_rate = _bitrate * 2;
                _codecCtx->rc_buffer_size = _bitrate;

                Logger.Info($"[LibAvEncoder] VP8 config: {_bitrate/1000}kbps, {_codecCtx->thread_count} threads, cpu-used=16");
                break;
        }
    }
}
