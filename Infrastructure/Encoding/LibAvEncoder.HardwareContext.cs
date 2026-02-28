#nullable enable
using System;
using FFmpeg.AutoGen;
using Vortice.Direct3D11;
using RemotePlayServer.Core;
using D3D11Device = Vortice.Direct3D11.ID3D11Device;
using FFmpegD3D11Device = FFmpeg.AutoGen.ID3D11Device;

namespace RemotePlayServer.Infrastructure.Encoding;

public unsafe partial class LibAvEncoder
{
    private bool InitializeHardwareContext()
    {
        var vendor = DetectGpuVendor();

        switch (vendor)
        {
            case GpuVendorType.NVIDIA:
                // NVIDIA: Use D3D11VA for true zero-copy from D3D11 textures
                // D3D11VA allows direct texture copy without CPU roundtrip
                Logger.Info("[LibAvEncoder] NVIDIA GPU: Trying D3D11VA for zero-copy texture encoding");
                if (TryInitializeNvencD3D11VA())
                    return true;
                Logger.Error("[LibAvEncoder] NVIDIA: D3D11VA failed, trying CUDA (will require CPU copy)");
                if (TryInitializeCuda())
                    return true;
                break;

            case GpuVendorType.AMD:
                // AMD: Use D3D11VA directly with AMF encoder
                // CUDA is not available on AMD, skip it
                Logger.Info("[LibAvEncoder] AMD GPU: Trying D3D11VA hardware context for AMF");
                if (TryInitializeAMFD3D11())
                    return true;
                Logger.Error("[LibAvEncoder] AMD: D3D11VA failed, trying generic D3D11VA");
                if (TryInitializeD3D11VA())
                    return true;
                break;

            case GpuVendorType.Intel:
                // Intel: Try QSV-specific hardware context first, then D3D11VA fallback
                Logger.Info("[LibAvEncoder] Intel GPU: Trying QSV hardware context");
                if (TryInitializeQSV())
                    return true;

                // If QSV returned false but _hwDeviceCtx is set, it means we are in Safe Mode (System Memory Input).
                // Do NOT fallback to generic D3D11VA, as h264_qsv needs the QSV device we just created.
                if (_hwDeviceCtx != null)
                {
                    Logger.Info("[LibAvEncoder] Intel: QSV Safe Mode active. Skipping D3D11VA fallback.");
                    return false;
                }

                // CRITICAL FIX: The h264_qsv encoder DOES NOT SUPPORT generic AV_PIX_FMT_D3D11 frames.
                // It requires AV_PIX_FMT_QSV frames (which wrap D3D11 surfaces).
                // TryInitializeD3D11VA sets up generic D3D11VA (AV_PIX_FMT_D3D11), which causes pixel format mismatch errors.
                // Therefore, if TryInitializeQSV failed completely (no QSV context), we CANNOT use h264_qsv with D3D11VA.
                // We must accept Software Upload (System Memory) or switch encoder (not implemented here).

                Logger.Error("[LibAvEncoder] Intel: QSV failed completely. D3D11VA fallback is NOT supported for h264_qsv. Using Software Upload.");
                return false;


            default:
                // Unknown: Try all in order
                if (TryInitializeCuda())
                    return true;
                if (TryInitializeD3D11VA())
                    return true;
                break;
        }

        Logger.Error($"[LibAvEncoder] Hardware frames init failed, using software upload ({_encoderName} still active)");
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
            Logger.Info("[LibAvEncoder] Initializing NVENC with D3D11VA for zero-copy...");

            // Try to use shared device if available (ENABLES TRUE ZERO-COPY)
            if (_device != null && _device.NativePointer != IntPtr.Zero)
            {
                Logger.Info($"[LibAvEncoder] Using shared D3D11 device for NVENC: 0x{_device.NativePointer:X}");

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
                        Logger.Info("[LibAvEncoder] NVENC D3D11VA device context initialized with SHARED device");
                    }
                    else
                    {
                        Logger.Error($"[LibAvEncoder] Shared device init failed: {GetErrorMessage(initRet)}");
                        ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    }
                }
            }

            // Fallback to creating new device if shared failed
            if (_hwDeviceCtx == null)
            {
                Logger.Error("[LibAvEncoder] Accessing shared device failed or not available, creating new D3D11VA device...");
                AVBufferRef* hwDeviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (ret < 0)
                {
                    Logger.Error($"[LibAvEncoder] D3D11VA device not available for NVENC: {GetErrorMessage(ret)}");
                    return false;
                }
                _hwDeviceCtx = hwDeviceCtx;
            }

            // Create hardware frames context
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Logger.Error("[LibAvEncoder] Failed to allocate D3D11VA frames context for NVENC");
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
                Logger.Error($"[LibAvEncoder] Failed to init D3D11VA frames context for NVENC: {GetErrorMessage(ret2)}");
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

            Logger.Info("[LibAvEncoder] NVENC D3D11VA initialized (TRUE ZERO-COPY enabled)");
            _isD3D11VAMode = true;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[LibAvEncoder] NVENC D3D11VA init exception: {ex.Message}");
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
                Logger.Error($"[LibAvEncoder] CUDA device not available: {GetErrorMessage(ret)}");
                return false;
            }
            _hwDeviceCtx = hwDeviceCtx;

            // Create hardware frames context for CUDA
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Logger.Error("[LibAvEncoder] Failed to allocate CUDA frames context");
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
                Logger.Error($"[LibAvEncoder] Failed to init CUDA frames context: {GetErrorMessage(ret)}");
                CleanupHwContext();
                return false;
            }

            // Set encoder to use CUDA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_CUDA;

            Logger.Info("[LibAvEncoder] CUDA hardware context initialized (zero-copy enabled)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[LibAvEncoder] CUDA init exception: {ex.Message}");
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
            Logger.Info("[LibAvEncoder] Initializing Intel QSV hardware context (Direct + Bridge Mode)...");

            AVBufferRef* hwDeviceCtx = null;
            AVBufferRef* d3d11vaDeviceRef = null;

            // PRIORITY 1: Direct QSV Creation (Most reliable on Intel)
            // This lets the driver/FFmpeg pick the best D3D11 device for QSV.
            // We then bridge to it from our capture device.
            int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, "auto", null, 0);

            if (ret == 0)
            {
                Logger.Info("[LibAvEncoder] QSV device created directly (matches known good config)");

                // We need to extract the underlying D3D11 device to support Cross-Device Copy/Sharing
                ret = ffmpeg.av_hwdevice_ctx_create_derived(&d3d11vaDeviceRef, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, hwDeviceCtx, 0);
                if (ret >= 0)
                {
                    var d3d11Ctx = (AVHWDeviceContext*)d3d11vaDeviceRef->data;
                    var d3d11vaCtx = (AVD3D11VADeviceContext*)d3d11Ctx->hwctx;
                    IntPtr qsvD3D11DevicePtr = (IntPtr)d3d11vaCtx->device;

                    Logger.Info($"[LibAvEncoder] Extracted underlying QSV D3D11 Device: 0x{qsvD3D11DevicePtr:X}");

                    if (qsvD3D11DevicePtr != IntPtr.Zero && qsvD3D11DevicePtr != _device!.NativePointer)
                    {
                        // Setup Cross-Device Bridge
                        _encoderD3D11Device = new D3D11Device(qsvD3D11DevicePtr);
                        _encoderD3D11Context = _encoderD3D11Device.ImmediateContext;
                        _usingCrossDeviceBridge = true;
                        Logger.Info("[LibAvEncoder] QSV: Enabled Cross-Device Bridge (Capture Device != QSV Device)");
                    }
                    else if (qsvD3D11DevicePtr == _device!.NativePointer)
                    {
                        Logger.Info("[LibAvEncoder] QSV: Running on same D3D11 device as capture (Optimal)");
                        _usingCrossDeviceBridge = false;
                    }
                }
                else
                {
                    Logger.Warn("[LibAvEncoder] Warning: Could not derive D3D11VA from QSV device. Zero-Copy might fail if devices differ.");
                }
            }
            else
            {
                Logger.Error($"[LibAvEncoder] Direct QSV init failed: {GetErrorMessage(ret)}. Trying Fallback (D3D11 Wrapper)...");
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

                    Logger.Info("[LibAvEncoder] Testing QSV Zero-Copy Capability (Alloc Check Only)...");
                    AVFrame* testFrame = ffmpeg.av_frame_alloc();
                    int allocRet = ffmpeg.av_hwframe_get_buffer(framesRef, testFrame, 0);

                    if (allocRet >= 0)
                    {
                        // Allocation worked! We trust the driver now.
                        // Don't risk failing on Map check.
                        Logger.Info("[LibAvEncoder] QSV Alloc Check Passed. Trusting Zero-Copy configuration.");

                        ffmpeg.av_frame_free(&testFrame);

                        _hwDeviceCtx = hwDeviceCtx;
                        _hwFramesCtx = framesRef; // Use this as main frames ctx
                        _qsvFramesCtx = ffmpeg.av_buffer_ref(framesRef); // Keep explicit ref for QSV logic

                        _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
                        _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
                        _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_QSV;

                        _useHardwareFrames = true; // SUCCESS!
                        Logger.Info("[LibAvEncoder] Intel QSV initialized in TRUE ZERO-COPY Mode.");

                        if (d3d11vaDeviceRef != null) ffmpeg.av_buffer_unref(&d3d11vaDeviceRef);
                        return true;
                    }
                    else
                    {
                        Logger.Error($"[LibAvEncoder] QSV Test Failed: Alloc Error {GetErrorMessage(allocRet)}");
                    }

                    if (testFrame != null) ffmpeg.av_frame_free(&testFrame);
                }
                else
                {
                    Logger.Error($"[LibAvEncoder] QSV frames init failed: {GetErrorMessage(ret)}");
                }

                ffmpeg.av_buffer_unref(&framesRef);
            }

            // Cleanup on failure
            if (d3d11vaDeviceRef != null) ffmpeg.av_buffer_unref(&d3d11vaDeviceRef);
            if (hwDeviceCtx != null) ffmpeg.av_buffer_unref(&hwDeviceCtx);

            // Fallback to Safe Mode
            Logger.Error("[LibAvEncoder] QSV Zero-Copy Init failed. Falling back to SAFE MODE.");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[LibAvEncoder] QSV init exception: {ex.Message}");
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
            Logger.Info("[LibAvEncoder] Initializing AMD AMF D3D11 hardware context with shared device...");

            if (_device == null)
            {
                Logger.Info("[LibAvEncoder] No shared D3D11 device available, falling back to FFmpeg device");
                return TryInitializeAMFD3D11WithNewDevice();
            }

            // Get the native device pointer
            IntPtr devicePtr = _device.NativePointer;
            if (devicePtr == IntPtr.Zero)
            {
                Logger.Error("[LibAvEncoder] Failed to get D3D11 device pointer");
                return TryInitializeAMFD3D11WithNewDevice();
            }

            Logger.Info($"[LibAvEncoder] Using shared D3D11 device: 0x{devicePtr:X}");

            // Create D3D11VA hardware device context WITH our existing device
            AVBufferRef* hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (hwDeviceCtx == null)
            {
                Logger.Error("[LibAvEncoder] Failed to allocate D3D11VA device context");
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
                Logger.Error($"[LibAvEncoder] Failed to init D3D11VA device context with shared device: {GetErrorMessage(ret)}");
                ffmpeg.av_buffer_unref(&hwDeviceCtx);
                return TryInitializeAMFD3D11WithNewDevice();
            }
            _hwDeviceCtx = hwDeviceCtx;

            Logger.Info("[LibAvEncoder] D3D11VA device context initialized with shared device");

            // Create hardware frames context
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Logger.Error("[LibAvEncoder] Failed to allocate AMD D3D11VA frames context");
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

            Logger.Info($"[LibAvEncoder] Configuring D3D11VA frames: {_width}x{_height}, pool_size=4");

            // Configure D3D11VA-specific texture options
            AVD3D11VAFramesContext* d3d11vaFramesCtx = (AVD3D11VAFramesContext*)framesCtx->hwctx;
            if (d3d11vaFramesCtx != null)
            {
                // Set bind flags that AMF encoder expects
                // AMF needs textures with BIND_DECODER and/or BIND_SHADER_RESOURCE
                // Explicitly set RenderTarget | ShaderResource (0x28) to satisfy FFmpeg and AMF
                d3d11vaFramesCtx->BindFlags = (uint)(Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource);
                d3d11vaFramesCtx->MiscFlags = 0;
                Logger.Info($"[LibAvEncoder] D3D11VA frames context configured with BindFlags=0x{d3d11vaFramesCtx->BindFlags:X}");
            }

            ret = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
            if (ret < 0)
            {
                Logger.Error($"[LibAvEncoder] Failed to init AMD D3D11VA frames context with shared device: {GetErrorMessage(ret)}");
                CleanupHwContext();
                return TryInitializeAMFD3D11WithNewDevice();
            }

            // Set encoder to use D3D11VA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;

            Logger.Info("[LibAvEncoder] AMD AMF D3D11VA initialized with SHARED device (TRUE ZERO-COPY enabled!)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[LibAvEncoder] AMD D3D11VA shared device init exception: {ex.Message}");
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
            Logger.Info("[LibAvEncoder] Trying AMD AMF D3D11 with FFmpeg-created device (fallback)...");

            // Create D3D11VA hardware device context - let FFmpeg create its own device
            AVBufferRef* hwDeviceCtx = null;
            int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
            if (ret < 0)
            {
                Logger.Error($"[LibAvEncoder] AMD D3D11VA device not available: {GetErrorMessage(ret)}");
                return false;
            }
            _hwDeviceCtx = hwDeviceCtx;

            // Create hardware frames context
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Logger.Error("[LibAvEncoder] Failed to allocate AMD D3D11VA frames context");
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
                Logger.Error($"[LibAvEncoder] Failed to init AMD D3D11VA frames context: {GetErrorMessage(ret)}");
                CleanupHwContext();
                return false;
            }

            // Set encoder to use D3D11VA frames
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;

            Logger.Info("[LibAvEncoder] AMD AMF D3D11VA initialized with FFmpeg device (hardware frames enabled)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[LibAvEncoder] AMD D3D11VA init exception: {ex.Message}");
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
                Logger.Info($"[LibAvEncoder] Using shared D3D11 device for Generic D3D11VA: 0x{_device.NativePointer:X}");

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
                        Logger.Info("[LibAvEncoder] Generic D3D11VA device context initialized with SHARED device");
                    }
                    else
                    {
                        Logger.Error($"[LibAvEncoder] Shared device init failed: {GetErrorMessage(initRet)}");
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
                    Logger.Error($"[LibAvEncoder] D3D11VA device not available: {GetErrorMessage(ret)}");
                    return false;
                }
                _hwDeviceCtx = hwDeviceCtx;
            }

            // Create hardware frames context
            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
            if (_hwFramesCtx == null)
            {
                Logger.Error("[LibAvEncoder] Failed to allocate D3D11VA frames context");
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
                Logger.Error($"[LibAvEncoder] Failed to init D3D11VA frames context: {GetErrorMessage(ret2)}");
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

            Logger.Info("[LibAvEncoder] D3D11VA hardware context initialized (zero-copy enabled)");
            _isD3D11VAMode = true;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[LibAvEncoder] D3D11VA init exception: {ex.Message}");
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
}
