#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Encoding;

/// <summary>
/// C# wrapper for native AmfWrapper.dll
/// Provides true zero-copy H.264/H.265 encoding from D3D11 textures on AMD GPUs
/// </summary>
public unsafe class AmfNativeWrapper : ITextureEncoder
{
    private const string DLL_NAME = "AmfWrapper.dll";
    
    // Result codes from native library
    private const int AMF_WRAPPER_OK = 0;
    private const int AMF_WRAPPER_FAIL = 1;
    private const int AMF_WRAPPER_NOT_INITIALIZED = 2;
    private const int AMF_WRAPPER_INVALID_PARAM = 3;
    private const int AMF_WRAPPER_NO_OUTPUT = 4;
    
    #region P/Invoke Declarations
    
    // Callback delegate for encoded data
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AmfEncodedDataCallback(
        IntPtr data,
        uint size,
        long pts,
        int isKeyFrame,
        IntPtr userData
    );
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfIsAvailable();
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfCreateEncoder(
        out IntPtr outHandle,
        IntPtr d3d11Device,
        int width,
        int height,
        int fps,
        int bitrate
    );
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfSetEncodedDataCallback(
        IntPtr handle,
        AmfEncodedDataCallback callback,
        IntPtr userData
    );
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfEncodeTexture(
        IntPtr handle,
        IntPtr texture,
        int forceKeyframe
    );
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfFlush(IntPtr handle);
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfEncodeNV12Bytes(
        IntPtr handle,
        IntPtr nv12Data,
        int dataSize,
        int forceKeyframe
    );
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfDestroyEncoder(IntPtr handle);
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AmfGetLastError();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfSetBitrate(IntPtr handle, int bitrateKbps);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfSetFps(IntPtr handle, int fps);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfCreateEncoderBgra(
        out IntPtr outHandle,
        IntPtr d3d11Device,
        int width,
        int height,
        int fps,
        int bitrate
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfEncodeBgraTexture(
        IntPtr handle,
        IntPtr texture,
        int forceKeyframe
    );

    // H.265/HEVC Extended APIs
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfCreateEncoderEx(
        out IntPtr outHandle,
        IntPtr d3d11Device,
        int width,
        int height,
        int fps,
        int bitrate,
        int useHevc
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AmfCreateEncoderBgraEx(
        out IntPtr outHandle,
        IntPtr d3d11Device,
        int width,
        int height,
        int fps,
        int bitrate,
        int useHevc
    );

    #endregion
    
    #region Fields
    
    private IntPtr _handle;
    private AmfEncodedDataCallback? _nativeCallback;
    private GCHandle _callbackHandle;
    private bool _disposed;
    
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private bool _useBgraMode;
    private bool _useHevc;
    private byte[] _callbackBuffer = Array.Empty<byte>();

    // ── Keyframe diagnostic counters ──
    // Track how often the encoder actually emits IDR frames. AMF HEVC with
    // GOP_SIZE=0 (infinite GOP) is supposed to honor FORCE_PICTURE_TYPE for forced
    // IDRs, but evidence from production logs shows the forced-IDR rate does not
    // match the request rate. These counters let us verify whether forced requests
    // are actually producing keyframes or being silently ignored by the encoder.
    private long _encodedFrameCount;
    private long _nativeKeyframeCount;
    private long _overrideKeyframeCount;
    private long _lastDiagLogTicks;

    #endregion

    #region Properties

    public bool IsInitialized => _handle != IntPtr.Zero;
    public int Width => _width;
    public int Height => _height;
    public int CurrentBitrateKbps => _bitrate;
    public int CurrentFps => _fps;
    public VideoCodec CurrentCodec => _useHevc ? VideoCodec.H265 : VideoCodec.H264;

    /// <summary>
    /// AMF supports BGRA input directly (internal color conversion)
    /// </summary>
    public bool SupportsBgraInput => true;

    /// <summary>
    /// Set to true before Initialize to use H.265/HEVC codec instead of H.264
    /// </summary>
    public bool UseHevc { get => _useHevc; set => _useHevc = value; }

    /// <summary>
    /// True if encoder was initialized in BGRA mode
    /// </summary>
    public bool UsingBgraMode => _useBgraMode;

    #endregion
    
    #region Events
    
    /// <summary>
    /// Event fired when encoded NAL data is available.
    /// WARNING: The backing array is reused between calls — do NOT hold a reference after handler returns.
    /// </summary>
    public event Action<ArraySegment<byte>, bool, long>? OnEncodedData;

    /// <summary>
    /// Event fired when the encoder detects a scene-change pattern (current encoded frame
    /// is significantly larger than recent average — typically tab switch / window open /
    /// large UI update). Subscribers (e.g. streamer) should force the next encoded frame
    /// to be an IDR so the client gets a fresh reference and avoids "đè trùng" artifacts.
    /// </summary>
    public event Action? OnSceneChangeDetected;

    // ── Scene-change detection state ──
    // Tracks recent encoded frame sizes. When current size > SCENE_CHANGE_RATIO × median,
    // a scene change is flagged. Only fires after a minimum sample of frames (to avoid
    // false positives during startup).
    private const int SCENE_CHANGE_WINDOW = 10;       // sliding window of recent frame sizes
    private const double SCENE_CHANGE_RATIO = 2.5;    // current frame must be 2.5× median
    private const int SCENE_CHANGE_MIN_SAMPLES = 5;   // don't flag until we have history
    private const long SCENE_CHANGE_MIN_BYTES = 8000; // skip trivial small frames (noise)
    private readonly System.Collections.Generic.Queue<long> _recentFrameSizes = new(SCENE_CHANGE_WINDOW);
    private long _lastSceneChangeTicks;
    
    #endregion
    
    #region Static Methods
    
    /// <summary>
    /// Check if AmfWrapper.dll is available and AMF runtime is installed
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            return AmfIsAvailable() != 0;
        }
        catch (DllNotFoundException)
        {
            Logger.Warn("[AmfNativeWrapper] AmfWrapper.dll not found");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] IsAvailable check failed: {ex.Message}");
            return false;
        }
    }
    
    #endregion
    
    #region Public Methods
    
    /// <summary>
    /// Initialize the AMF encoder with D3D11 device for zero-copy encoding
    /// </summary>
    public bool Initialize(int width, int height, int fps, int bitrate, ID3D11Device device)
    {
        if (_handle != IntPtr.Zero) return true;
        
        try
        {
            _width = width;
            _height = height;
            _fps = fps;
            _bitrate = bitrate;
            
            Logger.Info($"[AmfNativeWrapper] Initializing {width}x{height} @ {fps}fps, {bitrate}kbps, codec={(_useHevc ? "HEVC" : "H264")}");
            
            int result = _useHevc
                ? AmfCreateEncoderEx(out _handle, device.NativePointer, width, height, fps, bitrate, 1)
                : AmfCreateEncoder(out _handle, device.NativePointer, width, height, fps, bitrate);
            
            if (result != AMF_WRAPPER_OK)
            {
                string error = GetLastError();
                Logger.Error($"[AmfNativeWrapper] AmfCreateEncoder failed: {error}");
                _handle = IntPtr.Zero;
                return false;
            }
            
            // Set up callback
            _nativeCallback = NativeCallback;
            _callbackHandle = GCHandle.Alloc(_nativeCallback);
            
            result = AmfSetEncodedDataCallback(_handle, _nativeCallback, IntPtr.Zero);
            if (result != AMF_WRAPPER_OK)
            {
                Logger.Error("[AmfNativeWrapper] Failed to set callback");
                Cleanup();
                return false;
            }

            Logger.Info($"[AmfNativeWrapper] Initialized successfully (zero-copy, codec={(_useHevc ? "HEVC" : "H264")})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] Initialize exception: {ex.Message}");
            Cleanup();
            return false;
        }
    }
    
    /// <summary>
    /// Encode a D3D11 NV12 texture (zero-copy)
    /// </summary>
    public bool EncodeTexture(ID3D11Texture2D texture, bool forceKeyframe = false)
    {
        if (_handle == IntPtr.Zero || _disposed) return false;
        
        try
        {
            int result = AmfEncodeTexture(_handle, texture.NativePointer, forceKeyframe ? 1 : 0);
            return result == AMF_WRAPPER_OK;
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] EncodeTexture exception: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Encode NV12 byte array (uploads to GPU then encodes - not true zero-copy but still uses hardware encoder)
    /// </summary>
    public bool EncodeNV12Bytes(byte[] nv12Data, bool forceKeyframe = false)
    {
        if (_handle == IntPtr.Zero || _disposed) return false;
        if (nv12Data == null || nv12Data.Length == 0) return false;
        
        try
        {
            fixed (byte* ptr = nv12Data)
            {
                int result = AmfEncodeNV12Bytes(_handle, (IntPtr)ptr, nv12Data.Length, forceKeyframe ? 1 : 0);
                return result == AMF_WRAPPER_OK;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] EncodeNV12Bytes exception: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Flush encoder to get remaining frames
    /// </summary>
    public void Flush()
    {
        if (_handle == IntPtr.Zero) return;

        try
        {
            AmfFlush(_handle);
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] Flush exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Dynamically change encoder bitrate
    /// </summary>
    public bool SetBitrate(int bitrateKbps)
    {
        if (_handle == IntPtr.Zero || _disposed) return false;
        if (bitrateKbps <= 0) return false;

        try
        {
            int result = AmfSetBitrate(_handle, bitrateKbps);
            if (result == AMF_WRAPPER_OK)
            {
                _bitrate = bitrateKbps;
                Logger.Info($"[AmfNativeWrapper] Bitrate changed to {bitrateKbps}kbps");
                return true;
            }
            else
            {
                string error = GetLastError();
                Logger.Error($"[AmfNativeWrapper] SetBitrate failed: {error}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] SetBitrate exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Dynamically change encoder FPS
    /// </summary>
    public bool SetFps(int fps)
    {
        if (_handle == IntPtr.Zero || _disposed) return false;
        if (fps <= 0) return false;

        try
        {
            int result = AmfSetFps(_handle, fps);
            if (result == AMF_WRAPPER_OK)
            {
                _fps = fps;
                Logger.Info($"[AmfNativeWrapper] FPS changed to {fps}");
                return true;
            }
            else
            {
                string error = GetLastError();
                Logger.Error($"[AmfNativeWrapper] SetFps failed: {error}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] SetFps exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Initialize the AMF encoder in BGRA mode - accepts BGRA textures directly.
    /// AMF handles color conversion internally in hardware.
    /// </summary>
    public bool InitializeBgra(int width, int height, int fps, int bitrate, ID3D11Device device)
    {
        if (_handle != IntPtr.Zero) return true;

        try
        {
            _width = width;
            _height = height;
            _fps = fps;
            _bitrate = bitrate;
            _useBgraMode = true;

            Logger.Info($"[AmfNativeWrapper] Initializing BGRA mode {width}x{height} @ {fps}fps, {bitrate}kbps, codec={(_useHevc ? "HEVC" : "H264")}");

            int result = _useHevc
                ? AmfCreateEncoderBgraEx(out _handle, device.NativePointer, width, height, fps, bitrate, 1)
                : AmfCreateEncoderBgra(out _handle, device.NativePointer, width, height, fps, bitrate);

            if (result != AMF_WRAPPER_OK)
            {
                string error = GetLastError();
                Logger.Error($"[AmfNativeWrapper] AmfCreateEncoderBgra failed: {error}");
                _handle = IntPtr.Zero;
                _useBgraMode = false;
                return false;
            }

            // Set up callback
            _nativeCallback = NativeCallback;
            _callbackHandle = GCHandle.Alloc(_nativeCallback);

            result = AmfSetEncodedDataCallback(_handle, _nativeCallback, IntPtr.Zero);
            if (result != AMF_WRAPPER_OK)
            {
                Logger.Error("[AmfNativeWrapper] Failed to set callback");
                Cleanup();
                _useBgraMode = false;
                return false;
            }

            Logger.Info($"[AmfNativeWrapper] Initialized BGRA mode successfully (zero-copy, codec={(_useHevc ? "HEVC" : "H264")})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] InitializeBgra exception: {ex.Message}");
            Cleanup();
            _useBgraMode = false;
            return false;
        }
    }

    /// <summary>
    /// Encode a D3D11 BGRA texture directly (zero-copy, AMF converts internally)
    /// Use with encoder initialized via InitializeBgra()
    /// </summary>
    public bool EncodeBgraTexture(ID3D11Texture2D bgraTexture, bool forceKeyframe = false)
    {
        if (_handle == IntPtr.Zero || _disposed) return false;
        if (!_useBgraMode)
        {
            Logger.Warn("[AmfNativeWrapper] EncodeBgraTexture called but encoder not in BGRA mode");
            return false;
        }

        try
        {
            int result = AmfEncodeBgraTexture(_handle, bgraTexture.NativePointer, forceKeyframe ? 1 : 0);
            return result == AMF_WRAPPER_OK;
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] EncodeBgraTexture exception: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Private Methods
    
    private void NativeCallback(IntPtr data, uint size, long pts, int isKeyFrame, IntPtr userData)
    {
        if (data == IntPtr.Zero || size == 0) return;

        try
        {
            int len = (int)size;
            // Reusable buffer — grows with 2x headroom then stabilizes.
            // Safe because callbacks are serialized by native encode mutex.
            if (len > _callbackBuffer.Length)
                _callbackBuffer = new byte[len * 2];

            Marshal.Copy(data, _callbackBuffer, 0, len);

            // Override isKeyFrame with managed-side NAL parsing.
            // Native DetectKeyframeHEVC/H264 in NalUtils.h only matches 4-byte Annex-B
            // start codes (00 00 00 01); AMF HEVC sometimes emits 3-byte start codes
            // (00 00 01) for IDR frames. If we trust the buggy native flag, the streamer
            // tags IDR frames as P-frames and the client never gets a fresh keyframe.
            //
            // Policy: trust native when it says keyframe=true (no need to re-scan).
            // When native says keyframe=false, double-check with managed parser and
            // override to true if an IDR/VPS/SPS/CRA NAL is found.
            bool reportKeyframe = isKeyFrame != 0;
            if (!reportKeyframe)
            {
                bool detected;
                unsafe
                {
                    fixed (byte* p = _callbackBuffer)
                    {
                        detected = KeyframeDetector.IsKeyframe(p, len, _useHevc);
                    }
                }
                if (detected)
                {
                    reportKeyframe = true;
                    Interlocked.Increment(ref _overrideKeyframeCount);
                    Logger.Info($"[AmfNativeWrapper] NAL parser overrode native isKeyFrame=0 → 1 (size={len}B)");
                }
            }
            else
            {
                Interlocked.Increment(ref _nativeKeyframeCount);
            }
            Interlocked.Increment(ref _encodedFrameCount);

            // Periodic diagnostic: log keyframe ratio every 5 seconds. Reveals whether
            // AMF HEVC encoder is producing the IDRs we request (it often doesn't when
            // GOP_SIZE=0 / infinite GOP, since intra-refresh is used for quality and
            // FORCED_PICTURE_TYPE may be silently ignored).
            long nowTicks = Environment.TickCount64;
            long lastTicks = Interlocked.Read(ref _lastDiagLogTicks);
            if (nowTicks - lastTicks > 5_000)
            {
                if (Interlocked.CompareExchange(ref _lastDiagLogTicks, nowTicks, lastTicks) == lastTicks)
                {
                    long total = Interlocked.Read(ref _encodedFrameCount);
                    long nativeKf = Interlocked.Read(ref _nativeKeyframeCount);
                    long overrideKf = Interlocked.Read(ref _overrideKeyframeCount);
                    long totalKf = nativeKf + overrideKf;
                    double pct = total > 0 ? (totalKf * 100.0 / total) : 0;
                    Logger.Info($"[AmfNativeWrapper][diag] {_width}x{_height}@{_fps}fps codec={(_useHevc ? "HEVC" : "H264")}: " +
                                $"encoded={total} nativeKf={nativeKf} overrideKf={overrideKf} totalKf={totalKf} ({pct:F2}%)");
                }
            }

            // ── Scene-change detection ──
            // A normal IDR is ~200-450 KB at 1080p HEVC; P-frames are usually 5-80 KB.
            // When user clicks "New Tab" or switches apps, the next encoded frame is
            // dramatically larger than recent P-frames (could be 200+ KB delta because
            // the entire screen content changed). Detect this pattern and fire
            // OnSceneChangeDetected so the streamer can force the NEXT frame as IDR.
            //
            // The flag is consumed by SIPSorceryStreamer.OnEncodedData, which sets
            // ForceNextKeyframe=true so the following encode produces a fresh IDR.
            //
            // BUGFIX: was relying on DXGI's TotalMetadataBufferSize which underestimates
            // actual content change (e.g. New Tab opens a mostly-blank page with tiny
            // dirty rect header but huge visual difference). Encoded frame size is the
            // ground truth for "did the desktop content actually change".
            DetectAndFireSceneChange(len);

            OnEncodedData?.Invoke(new ArraySegment<byte>(_callbackBuffer, 0, len), reportKeyframe, pts);
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] Callback exception: {ex.Message}");
        }
    }
    
    private string GetLastError()
    {
        try
        {
            IntPtr errorPtr = AmfGetLastError();
            if (errorPtr != IntPtr.Zero)
            {
                return Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
            }
        }
        catch { }
        return "Unknown error";
    }

    /// <summary>
    /// Update recent-frame-size sliding window and fire <see cref="OnSceneChangeDetected"/>
    /// when the current frame's size is dramatically larger than the recent median.
    /// Called once per encoded frame from <see cref="NativeCallback"/>.
    /// </summary>
    private void DetectAndFireSceneChange(long currentSize)
    {
        // In Work / Efficiency Mode, P-frames handle local text/UI changes smoothly without keyframe spikes.
        if (RemotePlayServer.Configuration.EfficiencyConfig.Mode == RemotePlayServer.Configuration.StreamMode.Efficiency) return;

        // Don't flag trivial small frames — those are intra-refresh roll or noise.
        if (currentSize < SCENE_CHANGE_MIN_BYTES) return;

        // Add to sliding window.
        _recentFrameSizes.Enqueue(currentSize);
        if (_recentFrameSizes.Count > SCENE_CHANGE_WINDOW)
            _recentFrameSizes.Dequeue();

        // Need enough history to compute a meaningful median.
        if (_recentFrameSizes.Count < SCENE_CHANGE_MIN_SAMPLES) return;

        // Compute median (sorted copy).
        var sorted = new long[_recentFrameSizes.Count];
        _recentFrameSizes.CopyTo(sorted, 0);
        System.Array.Sort(sorted);
        long median = sorted[sorted.Length / 2];

        // Scene-change: current frame > RATIO × median.
        if (median > 0 && currentSize > SCENE_CHANGE_RATIO * median)
        {
            // Throttle: at most one scene-change flag per ~500ms (the encoder will
            // naturally produce an IDR within 1s after this anyway, and we don't
            // want to spam IDR requests on transient compression spikes).
            long now = Environment.TickCount64;
            if (now - _lastSceneChangeTicks >= 500)
            {
                _lastSceneChangeTicks = now;
                Logger.Info($"[AmfNativeWrapper] Scene change detected: frameSize={currentSize}B median={median}B ratio={currentSize / (double)median:F1}×");
                try { OnSceneChangeDetected?.Invoke(); }
                catch (Exception ex) { Logger.Error($"[AmfNativeWrapper] OnSceneChangeDetected handler error: {ex.Message}"); }
            }
        }
    }
    
    private void Cleanup()
    {
        try
        {
            if (_handle != IntPtr.Zero)
            {
                AmfDestroyEncoder(_handle);
                _handle = IntPtr.Zero;
            }
            
            if (_callbackHandle.IsAllocated)
            {
                _callbackHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[AmfNativeWrapper] Cleanup exception: {ex.Message}");
        }
    }
    
    #endregion
    
    #region IDisposable
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Cleanup();
        Logger.Info("[AmfNativeWrapper] Disposed");
    }
    
    #endregion
}
