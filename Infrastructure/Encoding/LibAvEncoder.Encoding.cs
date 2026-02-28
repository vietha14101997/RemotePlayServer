#nullable enable
using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.DXGI;
using Vortice.Direct3D11;
using RemotePlayServer.Core;
using D3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace RemotePlayServer.Infrastructure.Encoding;

public unsafe partial class LibAvEncoder
{
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
    public bool EncodeNV12(byte[] nv12Data, int width, int height, bool forceKeyframe = false)
    {
        if (!_initialized || _disposed) return false;

        lock (_lock)
        {
            try
            {
                // Handle resolution change
                if (width != _width || height != _height)
                {
                    Logger.Info($"[LibAvEncoder] Resolution changed {_width}x{_height} -> {width}x{height}");
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
                        Logger.Error("[LibAvEncoder] Failed to allocate sw frame");
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
                            Logger.Error($"[LibAvEncoder] Failed to alloc sw frame buffer: {GetErrorMessage(ret)}");
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
                            Logger.Error($"[LibAvEncoder] HW transfer failed: {GetErrorMessage(ret)}");
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
                        Logger.Info($"[LibAvEncoder] Frame not writable: {GetErrorMessage(ret)}");
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

                // Force keyframe if requested (for reconnect scenarios)
                // Hardware encoders (QSV/NVENC/AMF) ignore pict_type hint, need forced_idr option
                if (IsHardwareEncoder())
                {
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "forced_idr", forceKeyframe ? "1" : "0", 0);
                }
                _hwFrame->pict_type = forceKeyframe
                    ? AVPictureType.AV_PICTURE_TYPE_I
                    : AVPictureType.AV_PICTURE_TYPE_NONE;

                // Send frame to encoder
                ret = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
                if (ret < 0)
                {
                    Logger.Info($"[LibAvEncoder] Send frame error: {GetErrorMessage(ret)}");
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
                        Logger.Info($"[LibAvEncoder] Receive packet error: {GetErrorMessage(ret)}");
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
                Logger.Error($"[LibAvEncoder] Encode exception: {ex.Message}");
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
                Logger.Error($"[LibAvEncoder] EncodeTexture exception: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// TRUE ZERO-COPY: Encode D3D11 NV12 texture directly without CPU roundtrip.
    /// Only works when SupportsZeroCopyTexture is true (D3D11VA mode).
    /// Uses the capture device's D3D11 context to copy texture to FFmpeg's hardware frame.
    /// </summary>
    public bool EncodeD3D11TextureZeroCopy(D3D11Texture2D nv12Texture, bool forceKeyframe = false)
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
                        Logger.Error($"[LibAvEncoder] HW Error: get_buffer failed: {GetErrorMessage(ret)}");
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
                                Logger.Error($"[LibAvEncoder] HW Error: Map QSV -> D3D11 failed: {GetErrorMessage(mapRet)}");
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

                    // 3. Force keyframe if requested (for reconnect scenarios)
                    // Hardware encoders (QSV/NVENC/AMF) ignore pict_type hint, need forced_idr option
                    if (IsHardwareEncoder())
                    {
                        ffmpeg.av_opt_set(_codecCtx->priv_data, "forced_idr", forceKeyframe ? "1" : "0", 0);
                    }
                    _hwFrame->pict_type = forceKeyframe
                        ? AVPictureType.AV_PICTURE_TYPE_I
                        : AVPictureType.AV_PICTURE_TYPE_NONE;

                    // 4. Send Frame to Encoder
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

                        // Force keyframe if requested (for reconnect scenarios)
                        // Hardware encoders (QSV/NVENC/AMF) ignore pict_type hint, need forced_idr option
                        if (IsHardwareEncoder())
                        {
                            ffmpeg.av_opt_set(_codecCtx->priv_data, "forced_idr", forceKeyframe ? "1" : "0", 0);
                        }
                        swFrame->pict_type = forceKeyframe
                            ? AVPictureType.AV_PICTURE_TYPE_I
                            : AVPictureType.AV_PICTURE_TYPE_NONE;

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
                    Logger.Info($"[LibAvEncoder] Send frame error: {GetErrorMessage(ret)}");
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
                Logger.Error($"[LibAvEncoder] ZeroCopy encode exception: {ex.Message}");
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
}
