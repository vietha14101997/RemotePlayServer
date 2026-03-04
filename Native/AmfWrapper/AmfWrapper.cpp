// AmfWrapper.cpp - AMD AMF SDK Wrapper Implementation
// Provides zero-copy H.264/H.265 encoding from D3D11 textures

#define AMFWRAPPER_EXPORTS
#include "AmfWrapper.h"

#include <string>
#include <mutex>
#include <atomic>

// AMF SDK headers
#include "amf/public/include/core/Factory.h"
#include "amf/public/include/core/Context.h"
#include "amf/public/include/components/VideoEncoderVCE.h"
#include "amf/public/include/components/VideoEncoderHEVC.h"
#include "amf/public/common/AMFFactory.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

// Thread-safe error message
static thread_local std::string g_lastError;

// Detect keyframe for H.264: SPS (NAL type 7) or IDR (NAL type 5)
static int DetectKeyframeH264(const uint8_t* data, size_t size) {
    for (size_t i = 0; i + 4 < size; i++) {
        if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1) {
            int nalType = data[i+4] & 0x1F;
            if (nalType == 7 || nalType == 5) {
                return 1;
            }
        }
    }
    return 0;
}

// Detect keyframe for H.265: VPS (32), SPS (33), IDR_W_RADL (19), IDR_N_LP (20)
static int DetectKeyframeHEVC(const uint8_t* data, size_t size) {
    for (size_t i = 0; i + 5 < size; i++) {
        if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1) {
            int nalType = (data[i+4] >> 1) & 0x3F;
            // VPS=32, SPS=33, IDR_W_RADL=19, IDR_N_LP=20, CRA=21
            if (nalType == 32 || nalType == 33 || nalType == 19 || nalType == 20 || nalType == 21) {
                return 1;
            }
        }
    }
    return 0;
}

// Configure common encoder properties for low-latency streaming (H.264)
static void ConfigureAmfEncoderH264(amf::AMFComponentPtr& encoder, int fps, int bitrate) {
    encoder->SetProperty(AMF_VIDEO_ENCODER_USAGE, AMF_VIDEO_ENCODER_USAGE_LOW_LATENCY);
    encoder->SetProperty(AMF_VIDEO_ENCODER_QUALITY_PRESET, AMF_VIDEO_ENCODER_QUALITY_PRESET_BALANCED);
    encoder->SetProperty(AMF_VIDEO_ENCODER_PROFILE, AMF_VIDEO_ENCODER_PROFILE_BASELINE);
    encoder->SetProperty(AMF_VIDEO_ENCODER_PROFILE_LEVEL, 40);
    encoder->SetProperty(AMF_VIDEO_ENCODER_TARGET_BITRATE, bitrate * 1000);
    encoder->SetProperty(AMF_VIDEO_ENCODER_PEAK_BITRATE, bitrate * 1200);
    encoder->SetProperty(AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD, AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD_CBR);
    encoder->SetProperty(AMF_VIDEO_ENCODER_FRAMERATE, AMFConstructRate(fps, 1));
    encoder->SetProperty(AMF_VIDEO_ENCODER_B_PIC_PATTERN, 0);
    encoder->SetProperty(AMF_VIDEO_ENCODER_IDR_PERIOD, 0); // Infinite GOP (match Nvenc)
    encoder->SetProperty(AMF_VIDEO_ENCODER_LOWLATENCY_MODE, true);
    encoder->SetProperty(AMF_VIDEO_ENCODER_DE_BLOCKING_FILTER, true);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEADER_INSERTION_SPACING, 0);
    encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
    encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);
}

// Configure encoder properties for low-latency streaming (H.265/HEVC)
static void ConfigureAmfEncoderHEVC(amf::AMFComponentPtr& encoder, int fps, int bitrate) {
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_USAGE, AMF_VIDEO_ENCODER_HEVC_USAGE_LOW_LATENCY);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET, AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET_BALANCED);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_PROFILE, AMF_VIDEO_ENCODER_HEVC_PROFILE_MAIN);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_TIER, AMF_VIDEO_ENCODER_HEVC_TIER_MAIN);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_PROFILE_LEVEL, AMF_LEVEL_5_1);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_TARGET_BITRATE, bitrate * 1000);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_PEAK_BITRATE, bitrate * 1200);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_METHOD, AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_METHOD_CBR);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FRAMERATE, AMFConstructRate(fps, 1));
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_NUM_GOPS_PER_IDR, 1);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_GOP_SIZE, 0); // Infinite GOP (match Nvenc)
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_HEADER_INSERTION_MODE, AMF_VIDEO_ENCODER_HEVC_HEADER_INSERTION_MODE_IDR_ALIGNED);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_LOWLATENCY_MODE, true);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_DE_BLOCKING_FILTER_DISABLE, false);
    encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_INSERT_HEADER, true);
}

// Encoder context structure
struct AmfEncoderContext {
    amf::AMFContextPtr context;
    amf::AMFComponentPtr encoder;
    ID3D11Device* d3dDevice;

    int width;
    int height;
    int fps;
    int bitrate;

    AmfEncodedDataCallback callback;
    void* userData;

    int64_t pts;
    std::atomic<bool> initialized;
    std::mutex encodeMutex;

    bool useBgraInput;
    bool useHevc;

    AmfEncoderContext() : d3dDevice(nullptr), width(0), height(0), fps(0),
                          bitrate(0), callback(nullptr), userData(nullptr),
                          pts(0), initialized(false), useBgraInput(false), useHevc(false) {}
};

// Check AMF availability
AMFWRAPPER_API int AmfIsAvailable() {
    HMODULE amfDll = LoadLibraryW(L"amfrt64.dll");
    if (amfDll) {
        FreeLibrary(amfDll);
        return 1;
    }
    return 0;
}

// Internal: Create encoder with specified surface format and codec
static int AmfCreateEncoderInternal(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate,
    bool useBgra, bool useHevc)
{
    if (!outHandle || !d3d11Device || width <= 0 || height <= 0) {
        g_lastError = "Invalid parameters";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    *outHandle = nullptr;

    auto ctx = new AmfEncoderContext();
    ctx->d3dDevice = d3d11Device;
    ctx->width = width;
    ctx->height = height;
    ctx->fps = fps;
    ctx->bitrate = bitrate;
    ctx->useBgraInput = useBgra;
    ctx->useHevc = useHevc;

    AMF_RESULT res;

    // Initialize AMF factory
    res = g_AMFFactory.Init();
    if (res != AMF_OK) {
        g_lastError = "AMF Factory init failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }

    // Create AMF context
    res = g_AMFFactory.GetFactory()->CreateContext(&ctx->context);
    if (res != AMF_OK || !ctx->context) {
        g_lastError = "CreateContext failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }

    // Initialize with D3D11 device (CRITICAL for zero-copy)
    res = ctx->context->InitDX11(d3d11Device);
    if (res != AMF_OK) {
        g_lastError = "InitDX11 failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }

    // Create encoder component based on codec
    const wchar_t* codecId = useHevc ? AMFVideoEncoder_HEVC : AMFVideoEncoderVCE_AVC;
    res = g_AMFFactory.GetFactory()->CreateComponent(ctx->context, codecId, &ctx->encoder);
    if (res != AMF_OK || !ctx->encoder) {
        g_lastError = "CreateComponent failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }

    // Configure encoder properties
    if (useHevc) {
        ConfigureAmfEncoderHEVC(ctx->encoder, fps, bitrate);
    } else {
        ConfigureAmfEncoderH264(ctx->encoder, fps, bitrate);
    }

    // Initialize encoder with appropriate surface format
    amf::AMF_SURFACE_FORMAT fmt = useBgra ? amf::AMF_SURFACE_BGRA : amf::AMF_SURFACE_NV12;
    res = ctx->encoder->Init(fmt, width, height);
    if (res != AMF_OK) {
        g_lastError = "Encoder Init failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }

    ctx->initialized = true;
    *outHandle = ctx;

    return AMF_WRAPPER_OK;
}

// Create encoder (NV12 input, H.264)
AMFWRAPPER_API int AmfCreateEncoder(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate)
{
    return AmfCreateEncoderInternal(outHandle, d3d11Device, width, height, fps, bitrate, false, false);
}

// Create encoder with BGRA input support (H.264)
AMFWRAPPER_API int AmfCreateEncoderBgra(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate)
{
    return AmfCreateEncoderInternal(outHandle, d3d11Device, width, height, fps, bitrate, true, false);
}

// Create encoder with codec selection (NV12 input)
AMFWRAPPER_API int AmfCreateEncoderEx(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate, int useHevc)
{
    return AmfCreateEncoderInternal(outHandle, d3d11Device, width, height, fps, bitrate, false, useHevc != 0);
}

// Create encoder with BGRA input and codec selection
AMFWRAPPER_API int AmfCreateEncoderBgraEx(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate, int useHevc)
{
    return AmfCreateEncoderInternal(outHandle, d3d11Device, width, height, fps, bitrate, true, useHevc != 0);
}

// Set callback
AMFWRAPPER_API int AmfSetEncodedDataCallback(
    AmfEncoderHandle handle,
    AmfEncodedDataCallback callback,
    void* userData)
{
    if (!handle) return AMF_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<AmfEncoderContext*>(handle);
    ctx->callback = callback;
    ctx->userData = userData;

    return AMF_WRAPPER_OK;
}

// Internal: Submit surface to encoder and drain output (codec-aware)
static int AmfSubmitAndDrain(AmfEncoderContext* ctx, amf::AMFSurfacePtr& surface, int forceKeyframe) {
    AMF_RESULT res;

    // Set PTS
    surface->SetPts(ctx->pts);
    ctx->pts += 10000000 / ctx->fps;  // 100ns units

    // Force keyframe if requested (use correct property names per codec)
    if (forceKeyframe) {
        if (ctx->useHevc) {
            ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_HEVC_PICTURE_TYPE_IDR);
            ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_INSERT_HEADER, true);

            surface->SetProperty(AMF_VIDEO_ENCODER_HEVC_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_HEVC_PICTURE_TYPE_IDR);
            surface->SetProperty(AMF_VIDEO_ENCODER_HEVC_INSERT_HEADER, true);
        } else {
            ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
            ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
            ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);

            surface->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
            surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
            surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);
        }
    }

    // Submit to encoder
    res = ctx->encoder->SubmitInput(surface);
    if (res != AMF_OK && res != AMF_INPUT_FULL) {
        g_lastError = "SubmitInput failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    // Reset force picture type
    if (forceKeyframe) {
        if (ctx->useHevc) {
            ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_HEVC_PICTURE_TYPE_NONE);
        } else {
            ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_NONE);
        }
    }

    // Release surface reference early
    surface = nullptr;

    // Query ALL pending outputs (drain buffer to prevent memory buildup)
    amf::AMFDataPtr outputData;
    while (ctx->encoder->QueryOutput(&outputData) == AMF_OK && outputData) {
        amf::AMFBufferPtr buffer(outputData);
        if (buffer && ctx->callback) {
            uint8_t* data = static_cast<uint8_t*>(buffer->GetNative());
            size_t size = buffer->GetSize();
            int64_t pts = buffer->GetPts();
            int isKeyFrame = ctx->useHevc ? DetectKeyframeHEVC(data, size) : DetectKeyframeH264(data, size);
            ctx->callback(data, static_cast<uint32_t>(size), pts, isKeyFrame, ctx->userData);
        }
        outputData = nullptr;
    }

    return AMF_WRAPPER_OK;
}

// Flush
AMFWRAPPER_API int AmfFlush(AmfEncoderHandle handle) {
    if (!handle) return AMF_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<AmfEncoderContext*>(handle);
    if (!ctx->initialized) return AMF_WRAPPER_NOT_INITIALIZED;

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    ctx->encoder->Drain();

    // Get remaining frames
    amf::AMFDataPtr outputData;
    while (ctx->encoder->QueryOutput(&outputData) == AMF_OK && outputData) {
        amf::AMFBufferPtr buffer(outputData);
        if (buffer && ctx->callback) {
            uint8_t* data = static_cast<uint8_t*>(buffer->GetNative());
            size_t size = buffer->GetSize();
            int64_t pts = buffer->GetPts();
            ctx->callback(data, static_cast<uint32_t>(size), pts, 0, ctx->userData);
        }
        outputData = nullptr;
    }

    return AMF_WRAPPER_OK;
}

// Encode NV12 bytes (creates AMF surface and encodes)
AMFWRAPPER_API int AmfEncodeNV12Bytes(
    AmfEncoderHandle handle,
    const uint8_t* nv12Data,
    int dataSize,
    int forceKeyframe)
{
    if (!handle || !nv12Data || dataSize <= 0) {
        g_lastError = "Invalid parameters";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<AmfEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return AMF_WRAPPER_NOT_INITIALIZED;
    }

    int expectedSize = ctx->width * ctx->height * 3 / 2;
    if (dataSize < expectedSize) {
        g_lastError = "NV12 data size too small: " + std::to_string(dataSize) + " < " + std::to_string(expectedSize);
        return AMF_WRAPPER_INVALID_PARAM;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    AMF_RESULT res;

    // Allocate AMF surface
    amf::AMFSurfacePtr surface;
    res = ctx->context->AllocSurface(amf::AMF_MEMORY_HOST, amf::AMF_SURFACE_NV12, ctx->width, ctx->height, &surface);
    if (res != AMF_OK || !surface) {
        g_lastError = "AllocSurface failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    // Copy Y plane
    amf::AMFPlane* yPlane = surface->GetPlane(amf::AMF_PLANE_Y);
    if (yPlane) {
        uint8_t* yDst = static_cast<uint8_t*>(yPlane->GetNative());
        int yPitch = yPlane->GetHPitch();
        const uint8_t* ySrc = nv12Data;
        for (int y = 0; y < ctx->height; y++) {
            memcpy(yDst + y * yPitch, ySrc + y * ctx->width, ctx->width);
        }
    }

    // Copy UV plane
    amf::AMFPlane* uvPlane = surface->GetPlane(amf::AMF_PLANE_UV);
    if (uvPlane) {
        uint8_t* uvDst = static_cast<uint8_t*>(uvPlane->GetNative());
        int uvPitch = uvPlane->GetHPitch();
        const uint8_t* uvSrc = nv12Data + ctx->width * ctx->height;
        int uvHeight = ctx->height / 2;
        for (int y = 0; y < uvHeight; y++) {
            memcpy(uvDst + y * uvPitch, uvSrc + y * ctx->width, ctx->width);
        }
    }

    // Convert to DX11 memory for GPU encoding
    res = surface->Convert(amf::AMF_MEMORY_DX11);
    if (res != AMF_OK) {
        g_lastError = "Convert to DX11 failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    return AmfSubmitAndDrain(ctx, surface, forceKeyframe);
}

// Destroy
AMFWRAPPER_API int AmfDestroyEncoder(AmfEncoderHandle handle) {
    if (!handle) return AMF_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<AmfEncoderContext*>(handle);

    {
        std::lock_guard<std::mutex> lock(ctx->encodeMutex);
        ctx->initialized = false;

        if (ctx->encoder) {
            ctx->encoder->Terminate();
            ctx->encoder = nullptr;
        }

        if (ctx->context) {
            ctx->context->Terminate();
            ctx->context = nullptr;
        }
    }

    delete ctx;
    return AMF_WRAPPER_OK;
}

// TRUE ZERO-COPY: Encode directly from NV12 D3D11 texture
AMFWRAPPER_API int AmfEncodeTexture(AmfEncoderHandle handle, ID3D11Texture2D* nv12Texture, int forceKeyframe)
{
    if (!handle || !nv12Texture) {
        g_lastError = "Invalid parameters";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<AmfEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return AMF_WRAPPER_NOT_INITIALIZED;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    // Create AMF surface from D3D11 texture directly (TRUE ZERO-COPY)
    amf::AMFSurfacePtr surface;
    AMF_RESULT res = ctx->context->CreateSurfaceFromDX11Native(nv12Texture, &surface, nullptr);
    if (res != AMF_OK || !surface) {
        g_lastError = "CreateSurfaceFromDX11Native failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    return AmfSubmitAndDrain(ctx, surface, forceKeyframe);
}

// Dynamically change encoder bitrate (codec-aware)
AMFWRAPPER_API int AmfSetBitrate(AmfEncoderHandle handle, int bitrateKbps) {
    if (!handle) {
        g_lastError = "Invalid handle";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    if (bitrateKbps <= 0) {
        g_lastError = "Invalid bitrate";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<AmfEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return AMF_WRAPPER_NOT_INITIALIZED;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    AMF_RESULT res;

    if (ctx->useHevc) {
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_TARGET_BITRATE, bitrateKbps * 1000);
        if (res != AMF_OK) {
            g_lastError = "SetProperty HEVC_TARGET_BITRATE failed: " + std::to_string(res);
            return AMF_WRAPPER_FAIL;
        }
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_PEAK_BITRATE, bitrateKbps * 1200);
        if (res != AMF_OK) {
            g_lastError = "SetProperty HEVC_PEAK_BITRATE failed: " + std::to_string(res);
            return AMF_WRAPPER_FAIL;
        }
        ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_HEVC_PICTURE_TYPE_IDR);
    } else {
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_TARGET_BITRATE, bitrateKbps * 1000);
        if (res != AMF_OK) {
            g_lastError = "SetProperty TARGET_BITRATE failed: " + std::to_string(res);
            return AMF_WRAPPER_FAIL;
        }
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_PEAK_BITRATE, bitrateKbps * 1200);
        if (res != AMF_OK) {
            g_lastError = "SetProperty PEAK_BITRATE failed: " + std::to_string(res);
            return AMF_WRAPPER_FAIL;
        }
        ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
    }

    ctx->bitrate = bitrateKbps;
    return AMF_WRAPPER_OK;
}

// Dynamically change encoder FPS (codec-aware)
AMFWRAPPER_API int AmfSetFps(AmfEncoderHandle handle, int fps) {
    if (!handle) {
        g_lastError = "Invalid handle";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    if (fps <= 0) {
        g_lastError = "Invalid FPS";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<AmfEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return AMF_WRAPPER_NOT_INITIALIZED;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    AMF_RESULT res;

    if (ctx->useHevc) {
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FRAMERATE, AMFConstructRate(fps, 1));
        if (res != AMF_OK) {
            g_lastError = "SetProperty HEVC_FRAMERATE failed: " + std::to_string(res);
            return AMF_WRAPPER_FAIL;
        }
        ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_GOP_SIZE, fps * 2);
        ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_HEVC_PICTURE_TYPE_IDR);
    } else {
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FRAMERATE, AMFConstructRate(fps, 1));
        if (res != AMF_OK) {
            g_lastError = "SetProperty FRAMERATE failed: " + std::to_string(res);
            return AMF_WRAPPER_FAIL;
        }
        ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_IDR_PERIOD, fps * 2);
        ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
    }

    ctx->fps = fps;
    return AMF_WRAPPER_OK;
}

// Encode BGRA D3D11 texture directly (zero-copy, AMF converts internally)
AMFWRAPPER_API int AmfEncodeBgraTexture(AmfEncoderHandle handle, ID3D11Texture2D* bgraTexture, int forceKeyframe)
{
    if (!handle || !bgraTexture) {
        g_lastError = "Invalid parameters";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<AmfEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return AMF_WRAPPER_NOT_INITIALIZED;
    }

    if (!ctx->useBgraInput) {
        g_lastError = "Encoder not in BGRA mode";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    // Create AMF surface from BGRA D3D11 texture directly (TRUE ZERO-COPY)
    amf::AMFSurfacePtr surface;
    AMF_RESULT res = ctx->context->CreateSurfaceFromDX11Native(bgraTexture, &surface, nullptr);
    if (res != AMF_OK || !surface) {
        g_lastError = "CreateSurfaceFromDX11Native (BGRA) failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    return AmfSubmitAndDrain(ctx, surface, forceKeyframe);
}

// Get last error
AMFWRAPPER_API const char* AmfGetLastError() {
    return g_lastError.c_str();
}
