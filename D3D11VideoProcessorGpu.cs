#nullable enable
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// D3D11 Video Processor for true GPU-accelerated BGRA to NV12 conversion.
/// Uses ID3D11VideoDevice and ID3D11VideoProcessor for zero-copy color conversion.
/// </summary>
public sealed class D3D11VideoProcessorGpu : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    // Video interfaces
    private IntPtr _videoDevice;
    private IntPtr _videoContext;
    private IntPtr _videoProcessorEnum;
    private IntPtr _videoProcessor;

    // Output texture (NV12)
    private ID3D11Texture2D? _nv12Texture;
    private IntPtr _outputView;

    private readonly int _width;
    private readonly int _height;
    private bool _disposed;
    private bool _initialized;

    public ID3D11Texture2D? OutputTexture => _nv12Texture;
    public bool IsAvailable => _initialized;

    #region P/Invoke for D3D11 Video

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_VIDEO_PROCESSOR_CONTENT_DESC
    {
        public int InputFrameFormat;      // D3D11_VIDEO_FRAME_FORMAT
        public DXGI_RATIONAL InputFrameRate;
        public uint InputWidth;
        public uint InputHeight;
        public DXGI_RATIONAL OutputFrameRate;
        public uint OutputWidth;
        public uint OutputHeight;
        public int Usage;                 // D3D11_VIDEO_USAGE
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
    {
        public uint FourCC;
        public int ViewDimension;         // D3D11_VPIV_DIMENSION
        public D3D11_TEX2D_VPIV Texture2D;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_TEX2D_VPIV
    {
        public uint MipSlice;
        public uint ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
    {
        public int ViewDimension;         // D3D11_VPOV_DIMENSION
        public D3D11_TEX2D_VPOV Texture2D;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_TEX2D_VPOV
    {
        public uint MipSlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_VIDEO_PROCESSOR_STREAM
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;
        public uint OutputIndex;
        public uint InputFrameOrField;
        public uint PastFrames;
        public uint FutureFrames;
        public IntPtr ppPastSurfaces;
        public IntPtr pInputSurface;      // ID3D11VideoProcessorInputView*
        public IntPtr ppFutureSurfaces;
        public IntPtr ppPastSurfacesRight;
        public IntPtr pInputSurfaceRight;
        public IntPtr ppFutureSurfacesRight;
    }

    // D3D11 Video Device interface
    static readonly Guid IID_ID3D11VideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    static readonly Guid IID_ID3D11VideoContext = new("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");

    // Constants
    const int D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE = 0;
    const int D3D11_VIDEO_USAGE_PLAYBACK_NORMAL = 0;
    const int D3D11_VPIV_DIMENSION_TEXTURE2D = 1;
    const int D3D11_VPOV_DIMENSION_TEXTURE2D = 1;

    #endregion

    public D3D11VideoProcessorGpu(ID3D11Device device, int width, int height)
    {
        _device = device;
        _context = device.ImmediateContext;
        // Video Processor requires even dimensions
        _width = width & ~1;  // Align down to even
        _height = height & ~1;

        try
        {
            Initialize();
            _initialized = true;
            Console.WriteLine($"[VP-GPU] Initialized: {_width}x{_height} BGRA→NV12");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VP-GPU] Initialization failed: {ex.Message}");
            _initialized = false;
        }
    }

    private unsafe void Initialize()
    {
        // Query ID3D11VideoDevice from D3D11 device using native pointer
        IntPtr devicePtr = _device.NativePointer;
        {
            var iidVideoDevice = IID_ID3D11VideoDevice;
            int hr = Marshal.QueryInterface(devicePtr, ref iidVideoDevice, out _videoDevice);
            if (hr < 0)
                throw new COMException("Failed to get ID3D11VideoDevice", hr);
        }

        // Query ID3D11VideoContext from device context using native pointer
        IntPtr contextPtr = _context.NativePointer;
        {
            var iidVideoContext = IID_ID3D11VideoContext;
            int hr = Marshal.QueryInterface(contextPtr, ref iidVideoContext, out _videoContext);
            if (hr < 0)
            {
                Marshal.Release(_videoDevice);
                _videoDevice = IntPtr.Zero;
                throw new COMException("Failed to get ID3D11VideoContext", hr);
            }
        }

        // Create video processor enumerator
        var contentDesc = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            InputWidth = (uint)_width,
            InputHeight = (uint)_height,
            OutputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            OutputWidth = (uint)_width,
            OutputHeight = (uint)_height,
            Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL
        };

        int result = CreateVideoProcessorEnumerator(_videoDevice, ref contentDesc, out _videoProcessorEnum);
        if (result < 0)
            throw new COMException("Failed to create video processor enumerator", result);

        // Create video processor
        result = CreateVideoProcessor(_videoDevice, _videoProcessorEnum, 0, out _videoProcessor);
        if (result < 0)
            throw new COMException("Failed to create video processor", result);

        // Create output NV12 texture for video processor
        // Video processor output view requires RenderTarget bind flag
        _nv12Texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

        // Create output view
        var outputViewDesc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
        {
            ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D,
            Texture2D = new D3D11_TEX2D_VPOV { MipSlice = 0 }
        };

        // Use NativePointer directly - no need to release
        IntPtr nv12Ptr = _nv12Texture.NativePointer;
        result = CreateVideoProcessorOutputView(_videoDevice, nv12Ptr, _videoProcessorEnum, ref outputViewDesc, out _outputView);
        if (result < 0)
            throw new COMException($"Failed to create output view: 0x{result:X8}", result);

        Console.WriteLine("[VP-GPU] Video processor created successfully");
    }

    /// <summary>
    /// Convert BGRA texture to NV12 using GPU video processor.
    /// Returns the NV12 texture for direct use by encoder.
    /// </summary>
    public ID3D11Texture2D? ProcessBgraToNv12(ID3D11Texture2D bgraTexture)
    {
        if (!_initialized || _nv12Texture == null || _videoContext == IntPtr.Zero)
            return null;

        try
        {
            // Create input view for BGRA texture
            var inputViewDesc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
            {
                FourCC = 0,
                ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D,
                Texture2D = new D3D11_TEX2D_VPIV { MipSlice = 0, ArraySlice = 0 }
            };

            // Use NativePointer directly
            IntPtr bgraPtr = bgraTexture.NativePointer;
            IntPtr inputView = IntPtr.Zero;

            int hr = CreateVideoProcessorInputView(_videoDevice, bgraPtr, _videoProcessorEnum, ref inputViewDesc, out inputView);
            if (hr < 0)
            {
                Console.WriteLine($"[VP-GPU] Failed to create input view: 0x{hr:X8}");
                return null;
            }

            try
            {

                // Setup stream
                var stream = new D3D11_VIDEO_PROCESSOR_STREAM
                {
                    Enable = true,
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    PastFrames = 0,
                    FutureFrames = 0,
                    ppPastSurfaces = IntPtr.Zero,
                    pInputSurface = inputView,
                    ppFutureSurfaces = IntPtr.Zero,
                    ppPastSurfacesRight = IntPtr.Zero,
                    pInputSurfaceRight = IntPtr.Zero,
                    ppFutureSurfacesRight = IntPtr.Zero
                };

                // Process (blit) - convert BGRA to NV12
                hr = VideoProcessorBlt(_videoContext, _videoProcessor, _outputView, 0, 1, ref stream);
                if (hr < 0)
                {
                    Console.WriteLine($"[VP-GPU] VideoProcessorBlt failed: 0x{hr:X8}");
                    return null;
                }

                return _nv12Texture;
            }
            finally
            {
                // Only release inputView (which we created via QueryInterface)
                // Don't release bgraPtr - it's a NativePointer, not an IUnknown reference
                if (inputView != IntPtr.Zero)
                    Marshal.Release(inputView);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VP-GPU] ProcessBgraToNv12 error: {ex.Message}");
            return null;
        }
    }

    #region Native Method Delegates

    // We need to call methods on the video interfaces via vtable
    // ID3D11VideoDevice vtable layout (after IUnknown: QueryInterface, AddRef, Release)
    // 3: CreateVideoDecoder
    // 4: CreateVideoProcessor
    // 5: CreateAuthenticatedChannel
    // 6: CreateCryptoSession
    // 7: CreateVideoDecoderOutputView
    // 8: CreateVideoProcessorInputView
    // 9: CreateVideoProcessorOutputView
    // 10: CreateVideoProcessorEnumerator
    // ...

    private unsafe int CreateVideoProcessorEnumerator(IntPtr videoDevice, ref D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc, out IntPtr ppEnum)
    {
        // Get vtable
        IntPtr* vtable = *(IntPtr**)videoDevice;
        // CreateVideoProcessorEnumerator is at index 10
        var func = Marshal.GetDelegateForFunctionPointer<CreateVideoProcessorEnumeratorDelegate>(vtable[10]);
        return func(videoDevice, ref desc, out ppEnum);
    }

    private unsafe int CreateVideoProcessor(IntPtr videoDevice, IntPtr pEnum, uint rateConversionIndex, out IntPtr ppVideoProcessor)
    {
        IntPtr* vtable = *(IntPtr**)videoDevice;
        // CreateVideoProcessor is at index 4
        var func = Marshal.GetDelegateForFunctionPointer<CreateVideoProcessorDelegate>(vtable[4]);
        return func(videoDevice, pEnum, rateConversionIndex, out ppVideoProcessor);
    }

    private unsafe int CreateVideoProcessorInputView(IntPtr videoDevice, IntPtr pResource, IntPtr pEnum, ref D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC desc, out IntPtr ppView)
    {
        IntPtr* vtable = *(IntPtr**)videoDevice;
        // CreateVideoProcessorInputView is at index 8
        var func = Marshal.GetDelegateForFunctionPointer<CreateVideoProcessorInputViewDelegate>(vtable[8]);
        return func(videoDevice, pResource, pEnum, ref desc, out ppView);
    }

    private unsafe int CreateVideoProcessorOutputView(IntPtr videoDevice, IntPtr pResource, IntPtr pEnum, ref D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC desc, out IntPtr ppView)
    {
        IntPtr* vtable = *(IntPtr**)videoDevice;
        // CreateVideoProcessorOutputView is at index 9
        var func = Marshal.GetDelegateForFunctionPointer<CreateVideoProcessorOutputViewDelegate>(vtable[9]);
        return func(videoDevice, pResource, pEnum, ref desc, out ppView);
    }

    private unsafe int VideoProcessorBlt(IntPtr videoContext, IntPtr pVideoProcessor, IntPtr pView, uint outputFrame, uint streamCount, ref D3D11_VIDEO_PROCESSOR_STREAM pStreams)
    {
        IntPtr* vtable = *(IntPtr**)videoContext;
        // VideoProcessorBlt is at index 22 in ID3D11VideoContext
        var func = Marshal.GetDelegateForFunctionPointer<VideoProcessorBltDelegate>(vtable[22]);
        return func(videoContext, pVideoProcessor, pView, outputFrame, streamCount, ref pStreams);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateVideoProcessorEnumeratorDelegate(IntPtr self, ref D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc, out IntPtr ppEnum);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateVideoProcessorDelegate(IntPtr self, IntPtr pEnum, uint rateConversionIndex, out IntPtr ppVideoProcessor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateVideoProcessorInputViewDelegate(IntPtr self, IntPtr pResource, IntPtr pEnum, ref D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC desc, out IntPtr ppView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateVideoProcessorOutputViewDelegate(IntPtr self, IntPtr pResource, IntPtr pEnum, ref D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC desc, out IntPtr ppView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int VideoProcessorBltDelegate(IntPtr self, IntPtr pVideoProcessor, IntPtr pView, uint outputFrame, uint streamCount, ref D3D11_VIDEO_PROCESSOR_STREAM pStreams);

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Save pointers and set to zero immediately to prevent double-release
        var outputView = _outputView;
        var videoProcessor = _videoProcessor;
        var videoProcessorEnum = _videoProcessorEnum;
        var videoContext = _videoContext;
        var videoDevice = _videoDevice;
        var nv12Texture = _nv12Texture;

        _outputView = IntPtr.Zero;
        _videoProcessor = IntPtr.Zero;
        _videoProcessorEnum = IntPtr.Zero;
        _videoContext = IntPtr.Zero;
        _videoDevice = IntPtr.Zero;
        _nv12Texture = null;

        // Only release if initialization was successful
        // This prevents crashes when trying to release invalid pointers
        if (_initialized)
        {
            // Release in reverse order of creation
            SafeRelease(outputView);
            SafeRelease(videoProcessor);
            SafeRelease(videoProcessorEnum);
            SafeRelease(videoContext);
            SafeRelease(videoDevice);
        }

        // Dispose Vortice texture (this is safe)
        try { nv12Texture?.Dispose(); } catch { }

        Console.WriteLine("[VP-GPU] Disposed");
    }

    private static void SafeRelease(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        try
        {
            Marshal.Release(ptr);
        }
        catch
        {
            // Ignore release errors - pointer may be invalid
        }
    }
}
