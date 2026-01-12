#nullable enable
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace RemotePlayServer.Encoding;

/// <summary>
/// C# wrapper for native NvencWrapper.dll
/// Provides true zero-copy H.264 encoding from D3D11 textures on NVIDIA GPUs
/// </summary>
public unsafe class NvencNativeWrapper : ITextureEncoder
{
    private const string DLL_NAME = "NvencWrapper.dll";

    // Result codes from native library
    private const int NVENC_WRAPPER_OK = 0;
    private const int NVENC_WRAPPER_FAIL = 1;
    private const int NVENC_WRAPPER_NOT_INITIALIZED = 2;
    private const int NVENC_WRAPPER_INVALID_PARAM = 3;
    private const int NVENC_WRAPPER_NO_OUTPUT = 4;

    #region P/Invoke Declarations

    // Callback delegate for encoded data
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NvencEncodedDataCallback(
        IntPtr data,
        uint size,
        long pts,
        int isKeyFrame,
        IntPtr userData
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencIsAvailable();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencCreateEncoder(
        out IntPtr outHandle,
        IntPtr d3d11Device,
        int width,
        int height,
        int fps,
        int bitrate
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencSetEncodedDataCallback(
        IntPtr handle,
        NvencEncodedDataCallback callback,
        IntPtr userData
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencEncodeTexture(
        IntPtr handle,
        IntPtr texture,
        int forceKeyframe
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencFlush(IntPtr handle);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencDestroyEncoder(IntPtr handle);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NvencGetLastError();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencSetBitrate(IntPtr handle, int bitrateKbps);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencSetFps(IntPtr handle, int fps);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencCreateEncoderBgra(
        out IntPtr outHandle,
        IntPtr d3d11Device,
        int width,
        int height,
        int fps,
        int bitrate
    );

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvencEncodeBgraTexture(
        IntPtr handle,
        IntPtr texture,
        int forceKeyframe
    );

    #endregion

    #region Fields

    private IntPtr _handle;
    private NvencEncodedDataCallback? _nativeCallback;
    private GCHandle _callbackHandle;
    private bool _disposed;

    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private bool _useBgraMode;

    #endregion

    #region Properties

    public bool IsInitialized => _handle != IntPtr.Zero;
    public int Width => _width;
    public int Height => _height;
    public int CurrentBitrateKbps => _bitrate;
    public int CurrentFps => _fps;

    /// <summary>
    /// NVENC supports BGRA input directly (no NV12 conversion needed)
    /// </summary>
    public bool SupportsBgraInput => true;

    /// <summary>
    /// True if encoder was initialized in BGRA mode (no color conversion needed)
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
    /// Check if NvencWrapper.dll is available and NVENC runtime is installed
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            return NvencIsAvailable() != 0;
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("[NvencNativeWrapper] NvencWrapper.dll not found");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] IsAvailable check failed: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Initialize the NVENC encoder with D3D11 device for zero-copy encoding
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

            Console.WriteLine($"[NvencNativeWrapper] Initializing {width}x{height} @ {fps}fps, {bitrate}kbps");

            int result = NvencCreateEncoder(
                out _handle,
                device.NativePointer,
                width,
                height,
                fps,
                bitrate
            );

            if (result != NVENC_WRAPPER_OK)
            {
                string error = GetLastError();
                Console.WriteLine($"[NvencNativeWrapper] NvencCreateEncoder failed: {error}");
                _handle = IntPtr.Zero;
                return false;
            }

            // Set up callback
            _nativeCallback = NativeCallback;
            _callbackHandle = GCHandle.Alloc(_nativeCallback);

            result = NvencSetEncodedDataCallback(_handle, _nativeCallback, IntPtr.Zero);
            if (result != NVENC_WRAPPER_OK)
            {
                Console.WriteLine("[NvencNativeWrapper] Failed to set callback");
                Cleanup();
                return false;
            }

            Console.WriteLine("[NvencNativeWrapper] Initialized successfully (zero-copy enabled)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] Initialize exception: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Initialize the NVENC encoder in BGRA mode - accepts BGRA textures directly without color conversion.
    /// This eliminates the need for GPU compute shader BGRA→NV12 conversion.
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

            Console.WriteLine($"[NvencNativeWrapper] Initializing BGRA mode {width}x{height} @ {fps}fps, {bitrate}kbps");

            int result = NvencCreateEncoderBgra(
                out _handle,
                device.NativePointer,
                width,
                height,
                fps,
                bitrate
            );

            if (result != NVENC_WRAPPER_OK)
            {
                string error = GetLastError();
                Console.WriteLine($"[NvencNativeWrapper] NvencCreateEncoderBgra failed: {error}");
                _handle = IntPtr.Zero;
                _useBgraMode = false;
                return false;
            }

            // Set up callback
            _nativeCallback = NativeCallback;
            _callbackHandle = GCHandle.Alloc(_nativeCallback);

            result = NvencSetEncodedDataCallback(_handle, _nativeCallback, IntPtr.Zero);
            if (result != NVENC_WRAPPER_OK)
            {
                Console.WriteLine("[NvencNativeWrapper] Failed to set callback");
                Cleanup();
                _useBgraMode = false;
                return false;
            }

            Console.WriteLine("[NvencNativeWrapper] Initialized BGRA mode successfully (zero-copy, no color conversion)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] InitializeBgra exception: {ex.Message}");
            Cleanup();
            _useBgraMode = false;
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
            int result = NvencEncodeTexture(_handle, texture.NativePointer, forceKeyframe ? 1 : 0);
            return result == NVENC_WRAPPER_OK;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] EncodeTexture exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Encode a D3D11 BGRA texture directly (zero-copy, no color conversion)
    /// Use with encoder initialized via InitializeBgra()
    /// </summary>
    public bool EncodeBgraTexture(ID3D11Texture2D bgraTexture, bool forceKeyframe = false)
    {
        if (_handle == IntPtr.Zero || _disposed) return false;
        if (!_useBgraMode)
        {
            Console.WriteLine("[NvencNativeWrapper] EncodeBgraTexture called but encoder not in BGRA mode");
            return false;
        }

        try
        {
            int result = NvencEncodeBgraTexture(_handle, bgraTexture.NativePointer, forceKeyframe ? 1 : 0);
            return result == NVENC_WRAPPER_OK;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] EncodeBgraTexture exception: {ex.Message}");
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
            NvencFlush(_handle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] Flush exception: {ex.Message}");
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
            int result = NvencSetBitrate(_handle, bitrateKbps);
            if (result == NVENC_WRAPPER_OK)
            {
                _bitrate = bitrateKbps;
                Console.WriteLine($"[NvencNativeWrapper] Bitrate changed to {bitrateKbps}kbps");
                return true;
            }
            else
            {
                Console.WriteLine($"[NvencNativeWrapper] SetBitrate failed: {GetLastError()}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] SetBitrate exception: {ex.Message}");
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
            int result = NvencSetFps(_handle, fps);
            if (result == NVENC_WRAPPER_OK)
            {
                _fps = fps;
                Console.WriteLine($"[NvencNativeWrapper] FPS changed to {fps}");
                return true;
            }
            else
            {
                Console.WriteLine($"[NvencNativeWrapper] SetFps failed: {GetLastError()}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] SetFps exception: {ex.Message}");
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
            Console.WriteLine($"[NvencNativeWrapper] Callback exception: {ex.Message}");
        }
    }

    private string GetLastError()
    {
        try
        {
            IntPtr errorPtr = NvencGetLastError();
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
                NvencDestroyEncoder(_handle);
                _handle = IntPtr.Zero;
            }

            if (_callbackHandle.IsAllocated)
            {
                _callbackHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NvencNativeWrapper] Cleanup exception: {ex.Message}");
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Cleanup();
        Console.WriteLine("[NvencNativeWrapper] Disposed");
    }

    #endregion
}
