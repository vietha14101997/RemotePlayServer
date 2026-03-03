#nullable enable
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using RemotePlayServer.Core.Interfaces;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Encoding;

/// <summary>
/// C# wrapper for native QsvWrapper.dll
/// Provides true zero-copy H.264/H.265 encoding from D3D11 textures on Intel GPUs (Quick Sync Video)
/// </summary>
public unsafe class QsvNativeWrapper : ITextureEncoder
{
    private const string DLL_NAME = "QsvWrapper.dll";

    // Result codes from native library
    private const int QSV_WRAPPER_OK = 0;
    private const int QSV_WRAPPER_FAIL = 1;
    private const int QSV_WRAPPER_NOT_INITIALIZED = 2;
    private const int QSV_WRAPPER_INVALID_PARAM = 3;
    private const int QSV_WRAPPER_NO_OUTPUT = 4;

    #region P/Invoke Declarations

    // Callback delegate for encoded data
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void QsvEncodedDataCallback(
        IntPtr data,
        uint size,
        long pts,
        int isKeyFrame,
        IntPtr userData
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvIsAvailable();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvCreateEncoder(
        out IntPtr outHandle,
        IntPtr d3d11Device,
        int width,
        int height,
        int fps,
        int bitrate
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvSetEncodedDataCallback(
        IntPtr handle,
        QsvEncodedDataCallback callback,
        IntPtr userData
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvEncodeTexture(
        IntPtr handle,
        IntPtr texture,
        int forceKeyframe
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvFlush(IntPtr handle);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvDestroyEncoder(IntPtr handle);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QsvGetLastError();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvSetBitrate(IntPtr handle, int bitrateKbps);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvSetFps(IntPtr handle, int fps);

    // H.265/HEVC Extended API
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QsvCreateEncoderEx(
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
    private QsvEncodedDataCallback? _nativeCallback;
    private GCHandle _callbackHandle;
    private bool _disposed;

    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private bool _useHevc;

    #endregion

    #region Properties

    public bool IsInitialized => _handle != IntPtr.Zero;
    public int Width => _width;
    public int Height => _height;
    public int CurrentBitrateKbps => _bitrate;
    public int CurrentFps => _fps;

    #endregion

    /// <summary>
    /// Set to true before Initialize to use H.265/HEVC codec instead of H.264
    /// </summary>
    public bool UseHevc { get => _useHevc; set => _useHevc = value; }

    #region Events

    /// <summary>
    /// Event fired when encoded H.264 data is available
    /// Parameters: (byte[] nalData, bool isKeyFrame, long pts)
    /// </summary>
    public event Action<byte[], bool, long>? OnEncodedData;

    #endregion

    #region Static Methods

    /// <summary>
    /// Check if QsvWrapper.dll is available and Intel Quick Sync is installed
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            return QsvIsAvailable() != 0;
        }
        catch (DllNotFoundException)
        {
            Logger.Warn("[QsvNativeWrapper] QsvWrapper.dll not found");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[QsvNativeWrapper] IsAvailable check failed: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Initialize the QSV encoder with D3D11 device for zero-copy encoding
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

            Logger.Info($"[QsvNativeWrapper] Initializing {width}x{height} @ {fps}fps, {bitrate}kbps, codec={(_useHevc ? "HEVC" : "H264")}");

            int result = _useHevc
                ? QsvCreateEncoderEx(out _handle, device.NativePointer, width, height, fps, bitrate, 1)
                : QsvCreateEncoder(out _handle, device.NativePointer, width, height, fps, bitrate);

            if (result != QSV_WRAPPER_OK)
            {
                string error = GetLastError();
                Logger.Error($"[QsvNativeWrapper] QsvCreateEncoder failed: {error}");
                _handle = IntPtr.Zero;
                return false;
            }

            // Set up callback
            _nativeCallback = NativeCallback;
            _callbackHandle = GCHandle.Alloc(_nativeCallback);

            result = QsvSetEncodedDataCallback(_handle, _nativeCallback, IntPtr.Zero);
            if (result != QSV_WRAPPER_OK)
            {
                Logger.Error("[QsvNativeWrapper] Failed to set callback");
                Cleanup();
                return false;
            }

            Logger.Info($"[QsvNativeWrapper] Initialized successfully (zero-copy, codec={(_useHevc ? "HEVC" : "H264")})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[QsvNativeWrapper] Initialize exception: {ex.Message}");
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
            int result = QsvEncodeTexture(_handle, texture.NativePointer, forceKeyframe ? 1 : 0);
            return result == QSV_WRAPPER_OK;
        }
        catch (Exception ex)
        {
            Logger.Error($"[QsvNativeWrapper] EncodeTexture exception: {ex.Message}");
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
            QsvFlush(_handle);
        }
        catch (Exception ex)
        {
            Logger.Error($"[QsvNativeWrapper] Flush exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Dynamically change encoder bitrate.
    /// NOTE: QSV (Intel Media Foundation) doesn't reliably support runtime bitrate changes.
    /// This method returns false to indicate the feature is not supported.
    /// </summary>
    public bool SetBitrate(int bitrateKbps)
    {
        // QSV encoder through Media Foundation doesn't reliably support runtime bitrate changes
        // Attempting to change bitrate mid-stream can cause "incompatible video parameters" errors
        // Return false to let the adaptive bitrate controller know this encoder doesn't support it
        Logger.Warn($"[QsvNativeWrapper] Runtime bitrate change not supported (requested: {bitrateKbps}kbps)");
        return false;
    }

    /// <summary>
    /// Dynamically change encoder FPS.
    /// NOTE: QSV (Intel Media Foundation) doesn't reliably support runtime FPS changes.
    /// This method returns false to indicate the feature is not supported.
    /// </summary>
    public bool SetFps(int fps)
    {
        // QSV encoder through Media Foundation doesn't reliably support runtime FPS changes
        // Return false to let the caller know this encoder doesn't support it
        Logger.Warn($"[QsvNativeWrapper] Runtime FPS change not supported (requested: {fps}fps)");
        return false;
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
            Logger.Error($"[QsvNativeWrapper] Callback exception: {ex.Message}");
        }
    }

    private string GetLastError()
    {
        try
        {
            IntPtr errorPtr = QsvGetLastError();
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
                QsvDestroyEncoder(_handle);
                _handle = IntPtr.Zero;
            }

            if (_callbackHandle.IsAllocated)
            {
                _callbackHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[QsvNativeWrapper] Cleanup exception: {ex.Message}");
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Cleanup();
        Logger.Info("[QsvNativeWrapper] Disposed");
    }

    #endregion
}
