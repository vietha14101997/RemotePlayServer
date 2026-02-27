// NvencWrapper.cpp - NVIDIA NVENC SDK Wrapper Implementation
// Provides zero-copy H.264 encoding from D3D11 textures using NVIDIA Video Codec SDK

#include "NvencWrapper.h"

#include <string>
#include <mutex>
#include <atomic>
#include <vector>

// NVENC SDK headers (from NVIDIA Video Codec SDK v13.0)
#include "nvEncodeAPI.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

// Thread-safe error message
static thread_local std::string g_lastError;

// NVENC API function pointers
typedef NVENCSTATUS(NVENCAPI* PNVENCODEAPICREATEINSTANCE)(NV_ENCODE_API_FUNCTION_LIST*);

// Detect keyframe by scanning for SPS (NAL type 7) or IDR (NAL type 5)
static int DetectKeyframe(const uint8_t* data, size_t size) {
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

// Encoder context structure
struct NvencEncoderContext {
    // NVENC handles
    void* encoder = nullptr;
    NV_ENCODE_API_FUNCTION_LIST nvenc = {};
    HMODULE nvencLib = nullptr;

    // D3D11 resources
    ID3D11Device* d3dDevice = nullptr;
    ID3D11DeviceContext* d3dContext = nullptr;

    // Registered input resources (NV12 mode: staging texture)
    NV_ENC_REGISTERED_PTR registeredResource = nullptr;
    NV_ENC_INPUT_PTR mappedResource = nullptr;
    ID3D11Texture2D* inputTexture = nullptr;

    // BGRA mode: cached texture registration to avoid per-frame register/unregister
    ID3D11Texture2D* cachedBgraTexture = nullptr;
    NV_ENC_REGISTERED_PTR cachedBgraResource = nullptr;

    // Output bitstream buffer
    NV_ENC_OUTPUT_PTR bitstreamBuffer = nullptr;

    // Encoder settings
    int width = 0;
    int height = 0;
    int fps = 0;
    int bitrate = 0;

    // Callback
    NvencEncodedDataCallback callback = nullptr;
    void* userData = nullptr;

    // State
    int64_t pts = 0;
    std::atomic<bool> initialized{ false };
    std::mutex encodeMutex;

    bool useBgraInput = false;
};

// Helper: Load NVENC library
static HMODULE LoadNvencLibrary() {
    HMODULE lib = LoadLibraryW(L"nvEncodeAPI64.dll");
    if (!lib) {
        lib = LoadLibraryW(L"nvEncodeAPI.dll");
    }
    return lib;
}

// Check NVENC availability
NVENCWRAPPER_API int NvencIsAvailable() {
    HMODULE nvencDll = LoadNvencLibrary();
    if (nvencDll) {
        auto createInstance = (PNVENCODEAPICREATEINSTANCE)GetProcAddress(nvencDll, "NvEncodeAPICreateInstance");
        FreeLibrary(nvencDll);
        return createInstance ? 1 : 0;
    }
    return 0;
}

// Configure encode config with common settings
static void ConfigureNvencConfig(NV_ENC_CONFIG& encodeConfig, int fps, int bitrate) {
    encodeConfig.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    encodeConfig.rcParams.averageBitRate = bitrate * 1000;
    encodeConfig.rcParams.maxBitRate = bitrate * 1200;
    encodeConfig.rcParams.vbvBufferSize = bitrate * 1000 / fps;
    encodeConfig.rcParams.vbvInitialDelay = encodeConfig.rcParams.vbvBufferSize;
    encodeConfig.gopLength = fps * 2;
    encodeConfig.frameIntervalP = 1;
    encodeConfig.encodeCodecConfig.h264Config.idrPeriod = encodeConfig.gopLength;
    encodeConfig.encodeCodecConfig.h264Config.repeatSPSPPS = 1;
    encodeConfig.profileGUID = NV_ENC_H264_PROFILE_MAIN_GUID;
}

// Internal: Create encoder with specified mode
static int NvencCreateEncoderInternal(
    NvencEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate,
    bool useBgra)
{
    if (!outHandle || !d3d11Device || width <= 0 || height <= 0) {
        g_lastError = "Invalid parameters";
        return NVENC_WRAPPER_INVALID_PARAM;
    }

    *outHandle = nullptr;

    auto ctx = new NvencEncoderContext();
    ctx->d3dDevice = d3d11Device;
    d3d11Device->AddRef();
    d3d11Device->GetImmediateContext(&ctx->d3dContext);
    ctx->width = width;
    ctx->height = height;
    ctx->fps = fps;
    ctx->bitrate = bitrate;
    ctx->useBgraInput = useBgra;

    NVENCSTATUS nvStatus;

    // Load NVENC library
    ctx->nvencLib = LoadNvencLibrary();
    if (!ctx->nvencLib) {
        g_lastError = "Failed to load nvEncodeAPI64.dll";
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }

    // Get API create instance function
    auto createInstance = (PNVENCODEAPICREATEINSTANCE)GetProcAddress(ctx->nvencLib, "NvEncodeAPICreateInstance");
    if (!createInstance) {
        g_lastError = "Failed to get NvEncodeAPICreateInstance";
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }

    // Initialize API function list
    ctx->nvenc.version = NV_ENCODE_API_FUNCTION_LIST_VER;
    nvStatus = createInstance(&ctx->nvenc);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "NvEncodeAPICreateInstance failed: " + std::to_string(nvStatus);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }

    // Open encode session with D3D11
    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS sessionParams = {};
    sessionParams.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
    sessionParams.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
    sessionParams.device = d3d11Device;
    sessionParams.apiVersion = NVENCAPI_VERSION;

    nvStatus = ctx->nvenc.nvEncOpenEncodeSessionEx(&sessionParams, &ctx->encoder);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncOpenEncodeSessionEx failed: " + std::to_string(nvStatus);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }

    // Get encoder GUID for H.264
    GUID encodeGuid = NV_ENC_CODEC_H264_GUID;
    GUID presetGuid = NV_ENC_PRESET_P1_GUID;

    // Get preset config with tuning info (SDK 12+)
    NV_ENC_PRESET_CONFIG presetConfig = {};
    presetConfig.version = NV_ENC_PRESET_CONFIG_VER;
    presetConfig.presetCfg.version = NV_ENC_CONFIG_VER;

    nvStatus = ctx->nvenc.nvEncGetEncodePresetConfigEx(ctx->encoder, encodeGuid, presetGuid, NV_ENC_TUNING_INFO_LOW_LATENCY, &presetConfig);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncGetEncodePresetConfigEx failed: " + std::to_string(nvStatus);
        ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }

    // Initialize encoder params
    NV_ENC_INITIALIZE_PARAMS initParams = {};
    initParams.version = NV_ENC_INITIALIZE_PARAMS_VER;
    initParams.encodeGUID = encodeGuid;
    initParams.presetGUID = presetGuid;
    initParams.encodeWidth = width;
    initParams.encodeHeight = height;
    initParams.darWidth = width;
    initParams.darHeight = height;
    initParams.frameRateNum = fps;
    initParams.frameRateDen = 1;
    initParams.enablePTD = 1;
    initParams.reportSliceOffsets = 0;
    initParams.enableSubFrameWrite = 0;
    initParams.maxEncodeWidth = width;
    initParams.maxEncodeHeight = height;
    initParams.tuningInfo = NV_ENC_TUNING_INFO_LOW_LATENCY;

    // Configure encoder settings
    NV_ENC_CONFIG encodeConfig = presetConfig.presetCfg;
    ConfigureNvencConfig(encodeConfig, fps, bitrate);
    initParams.encodeConfig = &encodeConfig;

    nvStatus = ctx->nvenc.nvEncInitializeEncoder(ctx->encoder, &initParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncInitializeEncoder failed: " + std::to_string(nvStatus);
        ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }

    // NV12 mode: create staging texture and register it
    if (!useBgra) {
        D3D11_TEXTURE2D_DESC texDesc = {};
        texDesc.Width = width;
        texDesc.Height = height;
        texDesc.MipLevels = 1;
        texDesc.ArraySize = 1;
        texDesc.Format = DXGI_FORMAT_NV12;
        texDesc.SampleDesc.Count = 1;
        texDesc.Usage = D3D11_USAGE_DEFAULT;
        texDesc.BindFlags = 0;
        texDesc.CPUAccessFlags = 0;
        texDesc.MiscFlags = 0;

        HRESULT hr = d3d11Device->CreateTexture2D(&texDesc, nullptr, &ctx->inputTexture);
        if (FAILED(hr)) {
            g_lastError = "CreateTexture2D failed: " + std::to_string(hr);
            ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
            FreeLibrary(ctx->nvencLib);
            delete ctx;
            return NVENC_WRAPPER_FAIL;
        }

        NV_ENC_REGISTER_RESOURCE regRes = {};
        regRes.version = NV_ENC_REGISTER_RESOURCE_VER;
        regRes.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
        regRes.width = width;
        regRes.height = height;
        regRes.pitch = 0;
        regRes.resourceToRegister = ctx->inputTexture;
        regRes.bufferFormat = NV_ENC_BUFFER_FORMAT_NV12;
        regRes.bufferUsage = NV_ENC_INPUT_IMAGE;

        nvStatus = ctx->nvenc.nvEncRegisterResource(ctx->encoder, &regRes);
        if (nvStatus != NV_ENC_SUCCESS) {
            g_lastError = "nvEncRegisterResource failed: " + std::to_string(nvStatus);
            ctx->inputTexture->Release();
            ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
            FreeLibrary(ctx->nvencLib);
            delete ctx;
            return NVENC_WRAPPER_FAIL;
        }
        ctx->registeredResource = regRes.registeredResource;
    }

    // Create output bitstream buffer
    NV_ENC_CREATE_BITSTREAM_BUFFER bsParams = {};
    bsParams.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;

    nvStatus = ctx->nvenc.nvEncCreateBitstreamBuffer(ctx->encoder, &bsParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncCreateBitstreamBuffer failed: " + std::to_string(nvStatus);
        if (!useBgra) {
            ctx->nvenc.nvEncUnregisterResource(ctx->encoder, ctx->registeredResource);
            ctx->inputTexture->Release();
        }
        ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }
    ctx->bitstreamBuffer = bsParams.bitstreamBuffer;

    ctx->initialized = true;
    *outHandle = ctx;

    return NVENC_WRAPPER_OK;
}

// Create encoder (NV12 input)
NVENCWRAPPER_API int NvencCreateEncoder(
    NvencEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate)
{
    return NvencCreateEncoderInternal(outHandle, d3d11Device, width, height, fps, bitrate, false);
}

// Create encoder with BGRA input support
NVENCWRAPPER_API int NvencCreateEncoderBgra(
    NvencEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate)
{
    return NvencCreateEncoderInternal(outHandle, d3d11Device, width, height, fps, bitrate, true);
}

// Set callback
NVENCWRAPPER_API int NvencSetEncodedDataCallback(
    NvencEncoderHandle handle,
    NvencEncodedDataCallback callback,
    void* userData)
{
    if (!handle) return NVENC_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<NvencEncoderContext*>(handle);
    ctx->callback = callback;
    ctx->userData = userData;

    return NVENC_WRAPPER_OK;
}

// Internal: Lock bitstream, detect keyframe, fire callback
static void NvencRetrieveOutput(NvencEncoderContext* ctx) {
    NV_ENC_LOCK_BITSTREAM lockParams = {};
    lockParams.version = NV_ENC_LOCK_BITSTREAM_VER;
    lockParams.outputBitstream = ctx->bitstreamBuffer;
    lockParams.doNotWait = 0;

    NVENCSTATUS nvStatus = ctx->nvenc.nvEncLockBitstream(ctx->encoder, &lockParams);
    if (nvStatus == NV_ENC_SUCCESS) {
        int isKeyFrame = (lockParams.pictureType == NV_ENC_PIC_TYPE_IDR) ? 1 : 0;
        const uint8_t* data = static_cast<const uint8_t*>(lockParams.bitstreamBufferPtr);
        uint32_t size = lockParams.bitstreamSizeInBytes;

        // Fallback: also check NAL units in case pictureType doesn't reflect IDR
        if (!isKeyFrame && size > 5) {
            isKeyFrame = DetectKeyframe(data, size);
        }

        if (ctx->callback) {
            ctx->callback(data, size, lockParams.outputTimeStamp, isKeyFrame, ctx->userData);
        }

        ctx->nvenc.nvEncUnlockBitstream(ctx->encoder, ctx->bitstreamBuffer);
    }
}

// Encode NV12 texture
NVENCWRAPPER_API int NvencEncodeTexture(NvencEncoderHandle handle, ID3D11Texture2D* nv12Texture, int forceKeyframe)
{
    if (!handle || !nv12Texture) {
        g_lastError = "Invalid parameters";
        return NVENC_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<NvencEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return NVENC_WRAPPER_NOT_INITIALIZED;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    NVENCSTATUS nvStatus;

    // Copy input texture to registered staging texture
    ctx->d3dContext->CopyResource(ctx->inputTexture, nv12Texture);

    // Map input resource
    NV_ENC_MAP_INPUT_RESOURCE mapRes = {};
    mapRes.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
    mapRes.registeredResource = ctx->registeredResource;

    nvStatus = ctx->nvenc.nvEncMapInputResource(ctx->encoder, &mapRes);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncMapInputResource failed: " + std::to_string(nvStatus);
        return NVENC_WRAPPER_FAIL;
    }
    ctx->mappedResource = mapRes.mappedResource;

    // Encode frame
    NV_ENC_PIC_PARAMS picParams = {};
    picParams.version = NV_ENC_PIC_PARAMS_VER;
    picParams.inputBuffer = ctx->mappedResource;
    picParams.bufferFmt = NV_ENC_BUFFER_FORMAT_NV12;
    picParams.inputWidth = ctx->width;
    picParams.inputHeight = ctx->height;
    picParams.outputBitstream = ctx->bitstreamBuffer;
    picParams.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
    picParams.inputTimeStamp = ctx->pts;
    ctx->pts += 10000000 / ctx->fps;

    if (forceKeyframe) {
        picParams.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
    }

    nvStatus = ctx->nvenc.nvEncEncodePicture(ctx->encoder, &picParams);

    // Unmap input resource
    ctx->nvenc.nvEncUnmapInputResource(ctx->encoder, ctx->mappedResource);
    ctx->mappedResource = nullptr;

    if (nvStatus != NV_ENC_SUCCESS && nvStatus != NV_ENC_ERR_NEED_MORE_INPUT) {
        g_lastError = "nvEncEncodePicture failed: " + std::to_string(nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    NvencRetrieveOutput(ctx);

    return NVENC_WRAPPER_OK;
}

// Encode BGRA texture directly with cached registration
NVENCWRAPPER_API int NvencEncodeBgraTexture(NvencEncoderHandle handle, ID3D11Texture2D* bgraTexture, int forceKeyframe)
{
    if (!handle || !bgraTexture) {
        g_lastError = "Invalid parameters";
        return NVENC_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<NvencEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return NVENC_WRAPPER_NOT_INITIALIZED;
    }

    if (!ctx->useBgraInput) {
        g_lastError = "Encoder not created in BGRA mode - use NvencCreateEncoderBgra";
        return NVENC_WRAPPER_INVALID_PARAM;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    NVENCSTATUS nvStatus;

    // Cache BGRA texture registration - only re-register when texture pointer changes
    if (bgraTexture != ctx->cachedBgraTexture) {
        // Unregister previous texture if any
        if (ctx->cachedBgraResource) {
            ctx->nvenc.nvEncUnregisterResource(ctx->encoder, ctx->cachedBgraResource);
            ctx->cachedBgraResource = nullptr;
            ctx->cachedBgraTexture = nullptr;
        }

        // Register new texture
        NV_ENC_REGISTER_RESOURCE regRes = {};
        regRes.version = NV_ENC_REGISTER_RESOURCE_VER;
        regRes.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
        regRes.width = ctx->width;
        regRes.height = ctx->height;
        regRes.pitch = 0;
        regRes.resourceToRegister = bgraTexture;
        regRes.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;
        regRes.bufferUsage = NV_ENC_INPUT_IMAGE;

        nvStatus = ctx->nvenc.nvEncRegisterResource(ctx->encoder, &regRes);
        if (nvStatus != NV_ENC_SUCCESS) {
            g_lastError = "nvEncRegisterResource (BGRA) failed: " + std::to_string(nvStatus);
            return NVENC_WRAPPER_FAIL;
        }

        ctx->cachedBgraTexture = bgraTexture;
        ctx->cachedBgraResource = regRes.registeredResource;
    }

    // Map the cached registered resource
    NV_ENC_MAP_INPUT_RESOURCE mapRes = {};
    mapRes.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
    mapRes.registeredResource = ctx->cachedBgraResource;

    nvStatus = ctx->nvenc.nvEncMapInputResource(ctx->encoder, &mapRes);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncMapInputResource failed: " + std::to_string(nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    // Encode frame
    NV_ENC_PIC_PARAMS picParams = {};
    picParams.version = NV_ENC_PIC_PARAMS_VER;
    picParams.inputBuffer = mapRes.mappedResource;
    picParams.bufferFmt = NV_ENC_BUFFER_FORMAT_ARGB;
    picParams.inputWidth = ctx->width;
    picParams.inputHeight = ctx->height;
    picParams.outputBitstream = ctx->bitstreamBuffer;
    picParams.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
    picParams.inputTimeStamp = ctx->pts;
    ctx->pts += 10000000 / ctx->fps;

    if (forceKeyframe) {
        picParams.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
    }

    nvStatus = ctx->nvenc.nvEncEncodePicture(ctx->encoder, &picParams);

    // Unmap (but keep registered for next frame)
    ctx->nvenc.nvEncUnmapInputResource(ctx->encoder, mapRes.mappedResource);

    if (nvStatus != NV_ENC_SUCCESS && nvStatus != NV_ENC_ERR_NEED_MORE_INPUT) {
        g_lastError = "nvEncEncodePicture failed: " + std::to_string(nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    NvencRetrieveOutput(ctx);

    return NVENC_WRAPPER_OK;
}

// Flush
NVENCWRAPPER_API int NvencFlush(NvencEncoderHandle handle) {
    if (!handle) return NVENC_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<NvencEncoderContext*>(handle);
    if (!ctx->initialized) return NVENC_WRAPPER_NOT_INITIALIZED;

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    NV_ENC_PIC_PARAMS picParams = {};
    picParams.version = NV_ENC_PIC_PARAMS_VER;
    picParams.encodePicFlags = NV_ENC_PIC_FLAG_EOS;

    ctx->nvenc.nvEncEncodePicture(ctx->encoder, &picParams);

    return NVENC_WRAPPER_OK;
}

// Destroy
NVENCWRAPPER_API int NvencDestroyEncoder(NvencEncoderHandle handle) {
    if (!handle) return NVENC_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<NvencEncoderContext*>(handle);

    {
        std::lock_guard<std::mutex> lock(ctx->encodeMutex);
        ctx->initialized = false;

        if (ctx->encoder) {
            if (ctx->bitstreamBuffer) {
                ctx->nvenc.nvEncDestroyBitstreamBuffer(ctx->encoder, ctx->bitstreamBuffer);
                ctx->bitstreamBuffer = nullptr;
            }

            // Cleanup NV12 mode resources
            if (ctx->registeredResource) {
                ctx->nvenc.nvEncUnregisterResource(ctx->encoder, ctx->registeredResource);
                ctx->registeredResource = nullptr;
            }

            // Cleanup cached BGRA registration
            if (ctx->cachedBgraResource) {
                ctx->nvenc.nvEncUnregisterResource(ctx->encoder, ctx->cachedBgraResource);
                ctx->cachedBgraResource = nullptr;
                ctx->cachedBgraTexture = nullptr;
            }

            ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
            ctx->encoder = nullptr;
        }

        if (ctx->inputTexture) {
            ctx->inputTexture->Release();
            ctx->inputTexture = nullptr;
        }

        if (ctx->d3dContext) {
            ctx->d3dContext->Release();
            ctx->d3dContext = nullptr;
        }

        if (ctx->d3dDevice) {
            ctx->d3dDevice->Release();
            ctx->d3dDevice = nullptr;
        }

        if (ctx->nvencLib) {
            FreeLibrary(ctx->nvencLib);
            ctx->nvencLib = nullptr;
        }
    }

    delete ctx;
    return NVENC_WRAPPER_OK;
}

// Get last error
NVENCWRAPPER_API const char* NvencGetLastError() {
    return g_lastError.c_str();
}

// Internal: Build reconfigure params with current settings
static void BuildReconfigParams(
    NvencEncoderContext* ctx,
    NV_ENC_RECONFIGURE_PARAMS& reconfigParams,
    NV_ENC_CONFIG& encodeConfig,
    int newFps, int newBitrate)
{
    reconfigParams = {};
    reconfigParams.version = NV_ENC_RECONFIGURE_PARAMS_VER;
    reconfigParams.forceIDR = 1;

    NV_ENC_INITIALIZE_PARAMS& reInitParams = reconfigParams.reInitEncodeParams;
    reInitParams.version = NV_ENC_INITIALIZE_PARAMS_VER;
    reInitParams.encodeGUID = NV_ENC_CODEC_H264_GUID;
    reInitParams.presetGUID = NV_ENC_PRESET_P1_GUID;
    reInitParams.encodeWidth = ctx->width;
    reInitParams.encodeHeight = ctx->height;
    reInitParams.darWidth = ctx->width;
    reInitParams.darHeight = ctx->height;
    reInitParams.frameRateNum = newFps;
    reInitParams.frameRateDen = 1;
    reInitParams.enablePTD = 1;
    reInitParams.maxEncodeWidth = ctx->width;
    reInitParams.maxEncodeHeight = ctx->height;
    reInitParams.tuningInfo = NV_ENC_TUNING_INFO_LOW_LATENCY;

    ConfigureNvencConfig(encodeConfig, newFps, newBitrate);
    reInitParams.encodeConfig = &encodeConfig;
}

// Set bitrate dynamically
NVENCWRAPPER_API int NvencSetBitrate(NvencEncoderHandle handle, int bitrateKbps) {
    if (!handle || bitrateKbps <= 0) {
        g_lastError = "Invalid parameters";
        return NVENC_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<NvencEncoderContext*>(handle);
    if (!ctx->initialized || !ctx->encoder) {
        g_lastError = "Encoder not initialized";
        return NVENC_WRAPPER_NOT_INITIALIZED;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    // Get preset config for the new settings
    NV_ENC_PRESET_CONFIG presetConfig = {};
    presetConfig.version = NV_ENC_PRESET_CONFIG_VER;
    presetConfig.presetCfg.version = NV_ENC_CONFIG_VER;

    NVENCSTATUS nvStatus = ctx->nvenc.nvEncGetEncodePresetConfigEx(
        ctx->encoder, NV_ENC_CODEC_H264_GUID, NV_ENC_PRESET_P1_GUID,
        NV_ENC_TUNING_INFO_LOW_LATENCY, &presetConfig);

    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncGetEncodePresetConfigEx failed: " + std::to_string(nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    NV_ENC_RECONFIGURE_PARAMS reconfigParams;
    NV_ENC_CONFIG encodeConfig = presetConfig.presetCfg;
    BuildReconfigParams(ctx, reconfigParams, encodeConfig, ctx->fps, bitrateKbps);

    nvStatus = ctx->nvenc.nvEncReconfigureEncoder(ctx->encoder, &reconfigParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncReconfigureEncoder failed: " + std::to_string(nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    ctx->bitrate = bitrateKbps;
    return NVENC_WRAPPER_OK;
}

// Dynamically change encoder FPS
NVENCWRAPPER_API int NvencSetFps(NvencEncoderHandle handle, int fps) {
    if (!handle || fps <= 0) {
        g_lastError = "Invalid parameters";
        return NVENC_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<NvencEncoderContext*>(handle);
    if (!ctx->initialized || !ctx->encoder) {
        g_lastError = "Encoder not initialized";
        return NVENC_WRAPPER_NOT_INITIALIZED;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    // Get preset config
    NV_ENC_PRESET_CONFIG presetConfig = {};
    presetConfig.version = NV_ENC_PRESET_CONFIG_VER;
    presetConfig.presetCfg.version = NV_ENC_CONFIG_VER;

    NVENCSTATUS nvStatus = ctx->nvenc.nvEncGetEncodePresetConfigEx(
        ctx->encoder, NV_ENC_CODEC_H264_GUID, NV_ENC_PRESET_P1_GUID,
        NV_ENC_TUNING_INFO_LOW_LATENCY, &presetConfig);

    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncGetEncodePresetConfigEx failed: " + std::to_string(nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    NV_ENC_RECONFIGURE_PARAMS reconfigParams;
    NV_ENC_CONFIG encodeConfig = presetConfig.presetCfg;
    BuildReconfigParams(ctx, reconfigParams, encodeConfig, fps, ctx->bitrate);

    nvStatus = ctx->nvenc.nvEncReconfigureEncoder(ctx->encoder, &reconfigParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncReconfigureEncoder failed: " + std::to_string(nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    ctx->fps = fps;
    return NVENC_WRAPPER_OK;
}
