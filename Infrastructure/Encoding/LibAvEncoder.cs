#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.DXGI;
using SharpGen.Runtime;
using RemotePlayServer.Core;
// Use Vortice D3D11 types explicitly to avoid conflict with FFmpeg.AutoGen
using D3D11Device = Vortice.Direct3D11.ID3D11Device;
using D3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using D3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using Vortice.Direct3D11;
using FFmpegD3D11Device = FFmpeg.AutoGen.ID3D11Device;

namespace RemotePlayServer.Infrastructure.Encoding;


/// <summary>
/// Hardware-accelerated video encoder using FFmpeg libavcodec with D3D11VA.
/// Supports H.264 (AVC), H.265 (HEVC), VP9, and VP8 encoding with automatic fallback.
/// Provides zero-copy encoding from D3D11 textures to NAL units.
/// Supports NVIDIA NVENC, AMD AMF, Intel QSV hardware encoders, and libvpx software fallback.
/// </summary>
public unsafe partial class LibAvEncoder : IDisposable
{
    // P/Invoke for SetDllDirectory to add FFmpeg DLLs to search path
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;
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

        if (!System.IO.Directory.Exists(ffmpegPath))
        {
            // Try shared folder
            ffmpegPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "ffmpeg-master-latest-win64-gpl-shared", "bin");
        }

        if (System.IO.Directory.Exists(ffmpegPath))
        {
            // CRITICAL: Add FFmpeg bin folder to Windows DLL search path
            // This is required because FFmpeg.AutoGen uses DllImport which searches:
            // 1. Application directory (where .exe is)
            // 2. System directories
            // 3. Directories in PATH
            // But NOT subdirectories like "bin"

            // Method 1: SetDllDirectory - adds to DLL search path
            if (SetDllDirectory(ffmpegPath))
            {
                Logger.Info($"[LibAvEncoder] SetDllDirectory: {ffmpegPath}");
            }
            else
            {
                Logger.Error($"[LibAvEncoder] SetDllDirectory failed, error: {Marshal.GetLastWin32Error()}");
            }

            // Method 2: Also set FFmpeg.AutoGen RootPath (for its internal resolver)
            ffmpeg.RootPath = ffmpegPath;
            Logger.Info($"[LibAvEncoder] FFmpeg.RootPath: {ffmpegPath}");

            // Method 3: Add to PATH environment variable as fallback
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(ffmpegPath))
            {
                Environment.SetEnvironmentVariable("PATH", ffmpegPath + ";" + currentPath);
                Logger.Info($"[LibAvEncoder] Added to PATH: {ffmpegPath}");
            }
        }
        else
        {
            Logger.Warn($"[LibAvEncoder] WARNING: FFmpeg bin folder not found!");
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
                    Logger.Error("[LibAvEncoder] No hardware encoder found!");
                    return false;
                }

                Logger.Info($"[LibAvEncoder] Using encoder: {_encoderName}");

                // Allocate codec context
                _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecCtx == null)
                {
                    Logger.Error("[LibAvEncoder] Failed to allocate codec context");
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
                    Logger.Error($"[LibAvEncoder] Hardware frames init failed for {_encoderName}, using software upload");
                }

                // Open codec
                int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
                if (ret < 0)
                {
                    Logger.Error($"[LibAvEncoder] Failed to open codec: {GetErrorMessage(ret)}");
                    return false;
                }

                // Allocate packet
                _packet = ffmpeg.av_packet_alloc();
                if (_packet == null)
                {
                    Logger.Error("[LibAvEncoder] Failed to allocate packet");
                    return false;
                }

                // Allocate frame based on mode
                _hwFrame = ffmpeg.av_frame_alloc();
                if (_hwFrame == null)
                {
                    Logger.Error("[LibAvEncoder] Failed to allocate frame");
                    return false;
                }

                if (_useHardwareFrames)
                {
                    // Get hardware frame from pool
                    ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
                    if (ret < 0)
                    {
                        Logger.Error($"[LibAvEncoder] Failed to get hw frame buffer: {GetErrorMessage(ret)}");
                        // Fall back to software mode
                        _useHardwareFrames = false;
                    }
                    else
                    {
                        Logger.Info("[LibAvEncoder] Using D3D11 hardware frames (zero-copy)");
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
                        Logger.Error($"[LibAvEncoder] Failed to allocate frame buffer: {GetErrorMessage(ret)}");
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
                Logger.Info($"[LibAvEncoder] Initialized {_width}x{_height} @ {_fps}fps, {_bitrate/1000}kbps ({modeStr})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[LibAvEncoder] Init exception: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Current target bitrate in kbps.
    /// </summary>
    public int CurrentBitrateKbps => _bitrate / 1000;

    /// <summary>
    /// Dynamically change encoder bitrate without reinitialization.
    /// Works for most hardware encoders (NVENC, AMF, QSV) in VBR mode.
    /// </summary>
    /// <param name="bitrateKbps">New target bitrate in kbps.</param>
    /// <returns>True if bitrate was changed successfully.</returns>
    public bool SetBitrate(int bitrateKbps)
    {
        if (!_initialized || _disposed || _codecCtx == null) return false;

        // QSV through FFmpeg doesn't support runtime bitrate changes well
        // Return false to let caller know (won't crash, just skips adjustment)
        if (_encoderName.Contains("qsv"))
        {
            // Only log once to avoid spam
            Logger.Warn($"[LibAvEncoder] QSV encoder doesn't support runtime bitrate change (requested: {bitrateKbps}kbps)");
            return false;
        }

        lock (_lock)
        {
            int newBitrateBps = bitrateKbps * 1000;
            int oldBitrateBps = _bitrate;

            if (newBitrateBps == oldBitrateBps) return true; // No change needed

            try
            {
                // Update codec context bitrate parameters
                _codecCtx->bit_rate = newBitrateBps;
                _codecCtx->rc_max_rate = newBitrateBps * 3;     // VBR max = 3x target
                _codecCtx->rc_buffer_size = newBitrateBps / 2;  // 500ms buffer

                // For hardware encoders, try to update via av_opt_set
                // This may or may not work depending on the encoder and FFmpeg version
                if (IsHardwareEncoder())
                {
                    // NVENC
                    if (_encoderName.Contains("nvenc"))
                    {
                        ffmpeg.av_opt_set_int(_codecCtx->priv_data, "b", newBitrateBps, 0);
                        ffmpeg.av_opt_set_int(_codecCtx->priv_data, "maxrate", newBitrateBps * 3, 0);
                    }
                    // AMF
                    else if (_encoderName.Contains("amf"))
                    {
                        ffmpeg.av_opt_set_int(_codecCtx->priv_data, "target_bitrate", newBitrateBps, 0);
                        ffmpeg.av_opt_set_int(_codecCtx->priv_data, "peak_bitrate", newBitrateBps * 3, 0);
                    }
                }

                _bitrate = newBitrateBps;
                Logger.Info($"[LibAvEncoder] Bitrate changed: {oldBitrateBps / 1000} -> {bitrateKbps} kbps ({_encoderName})");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[LibAvEncoder] SetBitrate failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Check if current encoder is a hardware encoder that needs forced_idr option
    /// </summary>
    private bool IsHardwareEncoder()
    {
        return _encoderName.Contains("nvenc") ||
               _encoderName.Contains("amf") ||
               _encoderName.Contains("qsv");
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

        Logger.Info("[LibAvEncoder] Disposed (Safe)");
    }
}
