#nullable enable
using System;
using System.Runtime.InteropServices;
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

    #endregion

    #region Properties

    public bool IsInitialized => _handle != IntPtr.Zero;
    public int Width => _width;
    public int Height => _height;
    public int CurrentBitrateKbps => _bitrate;
    public int CurrentFps => _fps;

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
    /// Event fired when encoded H.264 data is available
    /// Parameters: (byte[] nalData, bool isKeyFrame, long pts)
    /// </summary>
    public event Action<byte[], bool, long>? OnEncodedData;
    
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
            // Copy NAL data from native memory
            byte[] nalData = new byte[size];
            Marshal.Copy(data, nalData, 0, (int)size);
            
            // Fire event
            OnEncodedData?.Invoke(nalData, isKeyFrame != 0, pts);
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
