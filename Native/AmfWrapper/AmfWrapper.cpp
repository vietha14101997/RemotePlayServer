// AmfWrapper.cpp - AMD AMF SDK Wrapper Implementation
// Provides zero-copy H.264 encoding from D3D11 textures

#define AMFWRAPPER_EXPORTS
#include "AmfWrapper.h"

#include <string>
#include <mutex>
#include <atomic>
#include <fstream>

// Debug logging to file
static std::ofstream g_logFile;
static std::mutex g_logMutex;
static int64_t g_frameCount = 0;

static void LogDebug(const char* format, ...) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (!g_logFile.is_open()) {
        g_logFile.open("logs/amf_debug.log", std::ios::out | std::ios::trunc);
    }
    if (g_logFile.is_open()) {
        char buffer[1024];
        va_list args;
        va_start(args, format);
        vsnprintf(buffer, sizeof(buffer), format, args);
        va_end(args);
        g_logFile << buffer << std::endl;
        g_logFile.flush();
    }
}

// AMF SDK headers
#include "amf/public/include/core/Factory.h"
#include "amf/public/include/core/Context.h"
#include "amf/public/include/components/VideoEncoderVCE.h"
#include "amf/public/common/AMFFactory.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

// Thread-safe error message
static thread_local std::string g_lastError;

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

    // BGRA mode - when true, encoder accepts BGRA input directly
    bool useBgraInput;

    AmfEncoderContext() : d3dDevice(nullptr), width(0), height(0), fps(0),
                          bitrate(0), callback(nullptr), userData(nullptr),
                          pts(0), initialized(false), useBgraInput(false) {}
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

// Create encoder
AMFWRAPPER_API int AmfCreateEncoder(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate)
{
    if (!outHandle || !d3d11Device || width <= 0 || height <= 0) {
        g_lastError = "Invalid parameters";
        return AMF_WRAPPER_INVALID_PARAM;
    }
    
    *outHandle = nullptr;
    
    // Create context
    auto ctx = new AmfEncoderContext();
    ctx->d3dDevice = d3d11Device;
    
    // Use original dimensions - AMF internally handles alignment
    ctx->width = width;
    ctx->height = height;
    ctx->fps = fps;
    ctx->bitrate = bitrate;
    
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
    
    // Create H.264 encoder component
    res = g_AMFFactory.GetFactory()->CreateComponent(ctx->context, AMFVideoEncoderVCE_AVC, &ctx->encoder);
    if (res != AMF_OK || !ctx->encoder) {
        g_lastError = "CreateComponent failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }
    
    // Configure encoder for low-latency streaming with better quality
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_USAGE, AMF_VIDEO_ENCODER_USAGE_LOW_LATENCY);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_QUALITY_PRESET, AMF_VIDEO_ENCODER_QUALITY_PRESET_BALANCED);
    // BASELINE profile for WebRTC browser compatibility (Chrome only reliably supports Baseline/Constrained Baseline)
    // HIGH profile (CABAC/8x8) produces better quality but browsers may fail to decode
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_PROFILE, AMF_VIDEO_ENCODER_PROFILE_BASELINE);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_PROFILE_LEVEL, 40);  // Level 4.0 for Baseline 1080p60
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_TARGET_BITRATE, bitrate * 1000);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_PEAK_BITRATE, bitrate * 1200);  // Tighter peak for more consistent quality
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD, AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD_CBR);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FRAMERATE, AMFConstructRate(fps, 1));
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_B_PIC_PATTERN, 0); // No B-frames for low latency
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_IDR_PERIOD, fps * 2); // IDR every 2 seconds
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_LOWLATENCY_MODE, true);
    
    // Quality improvements for desktop/text streaming
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_DE_BLOCKING_FILTER, true);  // Reduce blocking artifacts
    // Note: CABAC not available in Baseline profile - uses CAVLC instead (less efficient but WebRTC compatible)
    
    // CRITICAL: Insert SPS/PPS with EVERY IDR frame for WebRTC compatibility
    // Without this, decoder will fail after first IDR because it lacks parameter sets
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEADER_INSERTION_SPACING, 0); // Insert SPS/PPS with every IDR
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);
    
    // Initialize encoder with NV12 format
    res = ctx->encoder->Init(amf::AMF_SURFACE_NV12, width, height);
    if (res != AMF_OK) {
        g_lastError = "Encoder Init failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }
    
    ctx->initialized = true;
    *outHandle = ctx;
    
    return AMF_WRAPPER_OK;
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

// NOTE: AmfEncodeTexture is defined at the end of the file

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
    
    // Validate data size: NV12 = width * height * 1.5
    int expectedSize = ctx->width * ctx->height * 3 / 2;
    if (dataSize < expectedSize) {
        g_lastError = "NV12 data size too small: " + std::to_string(dataSize) + " < " + std::to_string(expectedSize);
        return AMF_WRAPPER_INVALID_PARAM;
    }
    
    std::lock_guard<std::mutex> lock(ctx->encodeMutex);
    
    AMF_RESULT res;
    
    // Allocate AMF surface (this will use GPU memory)
    amf::AMFSurfacePtr surface;
    res = ctx->context->AllocSurface(amf::AMF_MEMORY_HOST, amf::AMF_SURFACE_NV12, ctx->width, ctx->height, &surface);
    if (res != AMF_OK || !surface) {
        g_lastError = "AllocSurface failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }
    
    // Copy NV12 data to surface
    // Y plane
    amf::AMFPlane* yPlane = surface->GetPlane(amf::AMF_PLANE_Y);
    if (yPlane) {
        uint8_t* yDst = static_cast<uint8_t*>(yPlane->GetNative());
        int yPitch = yPlane->GetHPitch();
        const uint8_t* ySrc = nv12Data;
        
        // Copy line by line (handles pitch alignment)
        for (int y = 0; y < ctx->height; y++) {
            memcpy(yDst + y * yPitch, ySrc + y * ctx->width, ctx->width);
        }
    }
    
    // UV plane
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
    
    // Set PTS
    surface->SetPts(ctx->pts);
    ctx->pts += 10000000 / ctx->fps;  // 100ns units

    // Force keyframe if requested - must also insert SPS/PPS for decoder
    if (forceKeyframe) {
        surface->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
        surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
        surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);
    }

    // Submit to encoder
    res = ctx->encoder->SubmitInput(surface);
    if (res != AMF_OK && res != AMF_INPUT_FULL) {
        g_lastError = "SubmitInput failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    // Release surface reference early to free memory
    surface = nullptr;

    // Query ALL pending outputs (drain buffer to prevent memory buildup)
    amf::AMFDataPtr outputData;
    while (ctx->encoder->QueryOutput(&outputData) == AMF_OK && outputData) {
        amf::AMFBufferPtr buffer(outputData);
        if (buffer) {
            uint8_t* data = static_cast<uint8_t*>(buffer->GetNative());
            size_t size = buffer->GetSize();
            int64_t pts = buffer->GetPts();
            
            // Check for keyframe (SPS NAL type 7 or IDR NAL type 5)
            int isKeyFrame = 0;
            for (size_t i = 0; i + 4 < size; i++) {
                if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1) {
                    int nalType = data[i+4] & 0x1F;
                    if (nalType == 7 || nalType == 5) {  // SPS or IDR slice
                        isKeyFrame = 1;
                        break;
                    }
                }
            }
            
            // Fire callback
            if (ctx->callback) {
                ctx->callback(data, static_cast<uint32_t>(size), pts, isKeyFrame, ctx->userData);
            }
        }
        outputData = nullptr;  // Release this output before getting next
    }
    
    return AMF_WRAPPER_OK;
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

    int64_t inputFrame = g_frameCount++;
    LogDebug("[AmfEncodeTexture] Input frame #%lld, forceKeyframe=%d", inputFrame, forceKeyframe);

    AMF_RESULT res;

    // Create AMF surface from D3D11 texture directly (TRUE ZERO-COPY!)
    amf::AMFSurfacePtr surface;
    res = ctx->context->CreateSurfaceFromDX11Native(nv12Texture, &surface, nullptr);
    if (res != AMF_OK || !surface) {
        g_lastError = "CreateSurfaceFromDX11Native failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    // Set PTS
    surface->SetPts(ctx->pts);
    ctx->pts += 10000000 / ctx->fps;  // 100ns units

    // Force keyframe if requested - must also insert SPS/PPS for decoder
    // NOTE: Per-frame properties on surface may be ignored by some AMF versions
    // Try setting on BOTH encoder and surface to ensure it works
    if (forceKeyframe) {
        LogDebug("[AmfEncodeTexture] Setting IDR properties for frame #%lld", inputFrame);

        // Method 1: Set on encoder (applies to next submitted frame)
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
        LogDebug("[AmfEncodeTexture] Encoder FORCE_PICTURE_TYPE result: %d", res);
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
        LogDebug("[AmfEncodeTexture] Encoder INSERT_SPS result: %d", res);
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);
        LogDebug("[AmfEncodeTexture] Encoder INSERT_PPS result: %d", res);

        // Method 2: Also set on surface (for AMF versions that read from surface)
        surface->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
        surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
        surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);
    }

    // Submit to encoder
    res = ctx->encoder->SubmitInput(surface);
    if (res != AMF_OK && res != AMF_INPUT_FULL) {
        g_lastError = "SubmitInput failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }
    LogDebug("[AmfEncodeTexture] SubmitInput result: %d", res);

    // Reset force picture type to let encoder decide for subsequent frames
    if (forceKeyframe) {
        ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_NONE);
    }

    // Release surface reference early
    surface = nullptr;

    // Query ALL pending outputs (drain buffer)
    amf::AMFDataPtr outputData;
    int outputCount = 0;
    while (ctx->encoder->QueryOutput(&outputData) == AMF_OK && outputData) {
        amf::AMFBufferPtr buffer(outputData);
        if (buffer) {
            uint8_t* data = static_cast<uint8_t*>(buffer->GetNative());
            size_t size = buffer->GetSize();
            int64_t pts = buffer->GetPts();

            // Scan ALL NAL types for debugging
            std::string nalTypes;
            int isKeyFrame = 0;
            for (size_t i = 0; i + 4 < size; i++) {
                if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1) {
                    int nalType = data[i+4] & 0x1F;
                    if (!nalTypes.empty()) nalTypes += ",";
                    nalTypes += std::to_string(nalType);
                    if (nalType == 7 || nalType == 5) {  // SPS or IDR slice
                        isKeyFrame = 1;
                    }
                }
            }

            LogDebug("[AmfEncodeTexture] Output #%d: size=%zu, pts=%lld, NALs=[%s], isKey=%d",
                     outputCount++, size, pts, nalTypes.c_str(), isKeyFrame);

            // Fire callback
            if (ctx->callback) {
                ctx->callback(data, static_cast<uint32_t>(size), pts, isKeyFrame, ctx->userData);
            }
        }
        outputData = nullptr;
    }

    return AMF_WRAPPER_OK;
}

// Dynamically change encoder bitrate
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

    // Update target bitrate (in bits/s)
    res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_TARGET_BITRATE, bitrateKbps * 1000);
    if (res != AMF_OK) {
        g_lastError = "SetProperty TARGET_BITRATE failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    // Update peak bitrate (slightly higher for quality headroom)
    res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_PEAK_BITRATE, bitrateKbps * 1200);
    if (res != AMF_OK) {
        g_lastError = "SetProperty PEAK_BITRATE failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    // Force IDR on next frame to apply new bitrate immediately
    res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
    if (res != AMF_OK) {
        // Non-fatal - bitrate still changed
        LogDebug("[AmfSetBitrate] Force IDR failed: %d", res);
    }

    ctx->bitrate = bitrateKbps;
    LogDebug("[AmfSetBitrate] Bitrate changed to %d kbps", bitrateKbps);

    return AMF_WRAPPER_OK;
}

// Create encoder with BGRA input support (no NV12 conversion needed)
// AMF internally converts BGRA to NV12 in hardware when submitting
AMFWRAPPER_API int AmfCreateEncoderBgra(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate)
{
    if (!outHandle || !d3d11Device || width <= 0 || height <= 0) {
        g_lastError = "Invalid parameters";
        return AMF_WRAPPER_INVALID_PARAM;
    }

    *outHandle = nullptr;

    // Create context
    auto ctx = new AmfEncoderContext();
    ctx->d3dDevice = d3d11Device;
    ctx->width = width;
    ctx->height = height;
    ctx->fps = fps;
    ctx->bitrate = bitrate;
    ctx->useBgraInput = true;  // Mark as BGRA mode

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

    // Create H.264 encoder component
    res = g_AMFFactory.GetFactory()->CreateComponent(ctx->context, AMFVideoEncoderVCE_AVC, &ctx->encoder);
    if (res != AMF_OK || !ctx->encoder) {
        g_lastError = "CreateComponent failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }

    // Configure encoder for low-latency streaming
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_USAGE, AMF_VIDEO_ENCODER_USAGE_LOW_LATENCY);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_QUALITY_PRESET, AMF_VIDEO_ENCODER_QUALITY_PRESET_BALANCED);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_PROFILE, AMF_VIDEO_ENCODER_PROFILE_BASELINE);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_PROFILE_LEVEL, 40);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_TARGET_BITRATE, bitrate * 1000);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_PEAK_BITRATE, bitrate * 1200);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD, AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD_CBR);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FRAMERATE, AMFConstructRate(fps, 1));
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_B_PIC_PATTERN, 0);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_IDR_PERIOD, fps * 2);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_LOWLATENCY_MODE, true);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_DE_BLOCKING_FILTER, true);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEADER_INSERTION_SPACING, 0);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
    ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);

    // Initialize encoder with BGRA format - AMF handles color conversion internally
    res = ctx->encoder->Init(amf::AMF_SURFACE_BGRA, width, height);
    if (res != AMF_OK) {
        g_lastError = "Encoder Init (BGRA) failed: " + std::to_string(res);
        delete ctx;
        return AMF_WRAPPER_FAIL;
    }

    ctx->initialized = true;
    *outHandle = ctx;

    LogDebug("[AmfCreateEncoderBgra] Created BGRA encoder %dx%d @ %d fps, %d kbps", width, height, fps, bitrate);

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

    int64_t inputFrame = g_frameCount++;
    LogDebug("[AmfEncodeBgraTexture] Input frame #%lld, forceKeyframe=%d", inputFrame, forceKeyframe);

    AMF_RESULT res;

    // Create AMF surface from BGRA D3D11 texture directly (TRUE ZERO-COPY!)
    amf::AMFSurfacePtr surface;
    res = ctx->context->CreateSurfaceFromDX11Native(bgraTexture, &surface, nullptr);
    if (res != AMF_OK || !surface) {
        g_lastError = "CreateSurfaceFromDX11Native (BGRA) failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }

    // Set PTS
    surface->SetPts(ctx->pts);
    ctx->pts += 10000000 / ctx->fps;  // 100ns units

    // Force keyframe if requested
    if (forceKeyframe) {
        LogDebug("[AmfEncodeBgraTexture] Setting IDR properties for frame #%lld", inputFrame);

        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
        res = ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);

        surface->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
        surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, true);
        surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, true);
    }

    // Submit to encoder - AMF internally converts BGRA to NV12 in hardware
    res = ctx->encoder->SubmitInput(surface);
    if (res != AMF_OK && res != AMF_INPUT_FULL) {
        g_lastError = "SubmitInput (BGRA) failed: " + std::to_string(res);
        return AMF_WRAPPER_FAIL;
    }
    LogDebug("[AmfEncodeBgraTexture] SubmitInput result: %d", res);

    // Reset force picture type
    if (forceKeyframe) {
        ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_NONE);
    }

    // Release surface reference early
    surface = nullptr;

    // Query ALL pending outputs
    amf::AMFDataPtr outputData;
    int outputCount = 0;
    while (ctx->encoder->QueryOutput(&outputData) == AMF_OK && outputData) {
        amf::AMFBufferPtr buffer(outputData);
        if (buffer) {
            uint8_t* data = static_cast<uint8_t*>(buffer->GetNative());
            size_t size = buffer->GetSize();
            int64_t pts = buffer->GetPts();

            // Scan NAL types
            std::string nalTypes;
            int isKeyFrame = 0;
            for (size_t i = 0; i + 4 < size; i++) {
                if (data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1) {
                    int nalType = data[i+4] & 0x1F;
                    if (!nalTypes.empty()) nalTypes += ",";
                    nalTypes += std::to_string(nalType);
                    if (nalType == 7 || nalType == 5) {
                        isKeyFrame = 1;
                    }
                }
            }

            LogDebug("[AmfEncodeBgraTexture] Output #%d: size=%zu, pts=%lld, NALs=[%s], isKey=%d",
                     outputCount++, size, pts, nalTypes.c_str(), isKeyFrame);

            if (ctx->callback) {
                ctx->callback(data, static_cast<uint32_t>(size), pts, isKeyFrame, ctx->userData);
            }
        }
        outputData = nullptr;
    }

    return AMF_WRAPPER_OK;
}

// Get last error
AMFWRAPPER_API const char* AmfGetLastError() {
    return g_lastError.c_str();
}
