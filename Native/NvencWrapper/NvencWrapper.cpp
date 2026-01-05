// NvencWrapper.cpp - NVIDIA NVENC SDK Wrapper Implementation
// Provides zero-copy H.264 encoding from D3D11 textures using NVIDIA Video Codec SDK

#include "NvencWrapper.h"

#include <string>
#include <mutex>
#include <atomic>
#include <vector>
#include <fstream>

// NVENC SDK headers (from NVIDIA Video Codec SDK v13.0)
#include "nvEncodeAPI.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

// Debug logging
static std::ofstream g_logFile;
static std::mutex g_logMutex;

static void LogDebug(const char* format, ...) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (!g_logFile.is_open()) {
        g_logFile.open("logs/nvenc_debug.log", std::ios::out | std::ios::trunc);
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

// Thread-safe error message
static thread_local std::string g_lastError;

// NVENC API function pointers
typedef NVENCSTATUS(NVENCAPI* PNVENCODEAPICREATEINSTANCE)(NV_ENCODE_API_FUNCTION_LIST*);

// Encoder context structure
struct NvencEncoderContext {
    // NVENC handles
    void* encoder = nullptr;
    NV_ENCODE_API_FUNCTION_LIST nvenc = {};
    HMODULE nvencLib = nullptr;

    // D3D11 resources
    ID3D11Device* d3dDevice = nullptr;
    ID3D11DeviceContext* d3dContext = nullptr;

    // Registered input resources
    NV_ENC_REGISTERED_PTR registeredResource = nullptr;
    NV_ENC_INPUT_PTR mappedResource = nullptr;
    ID3D11Texture2D* inputTexture = nullptr;

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

    // BGRA mode - when true, encoder accepts BGRA input directly
    bool useBgraInput = false;
};

// Helper: Load NVENC library
static HMODULE LoadNvencLibrary() {
    // Try different possible locations
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
        // Also verify we can get the API
        auto createInstance = (PNVENCODEAPICREATEINSTANCE)GetProcAddress(nvencDll, "NvEncodeAPICreateInstance");
        FreeLibrary(nvencDll);
        return createInstance ? 1 : 0;
    }
    return 0;
}

// Create encoder
NVENCWRAPPER_API int NvencCreateEncoder(
    NvencEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate)
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
    GUID presetGuid = NV_ENC_PRESET_P1_GUID;  // SDK 12+ low latency preset

    LogDebug("[NvencWrapper] Using SDK 13.0 API, preset P1");

    // Get preset config with tuning info (SDK 12+)
    NV_ENC_PRESET_CONFIG presetConfig = {};
    presetConfig.version = NV_ENC_PRESET_CONFIG_VER;
    presetConfig.presetCfg.version = NV_ENC_CONFIG_VER;

    LogDebug("[NvencWrapper] Calling nvEncGetEncodePresetConfigEx...");
    nvStatus = ctx->nvenc.nvEncGetEncodePresetConfigEx(ctx->encoder, encodeGuid, presetGuid, NV_ENC_TUNING_INFO_LOW_LATENCY, &presetConfig);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncGetEncodePresetConfigEx failed: " + std::to_string(nvStatus);
        LogDebug("[NvencWrapper] nvEncGetEncodePresetConfigEx failed: %d", nvStatus);
        ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }
    LogDebug("[NvencWrapper] nvEncGetEncodePresetConfigEx succeeded");

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
    initParams.enablePTD = 1;  // Enable picture type decision
    initParams.reportSliceOffsets = 0;
    initParams.enableSubFrameWrite = 0;
    initParams.maxEncodeWidth = width;
    initParams.maxEncodeHeight = height;
    initParams.tuningInfo = NV_ENC_TUNING_INFO_LOW_LATENCY;

    // Configure encoder settings - use preset config with minimal overrides
    NV_ENC_CONFIG encodeConfig = presetConfig.presetCfg;
    // Note: version is already set in presetCfg, don't override

    // Rate control - CBR for streaming
    encodeConfig.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    encodeConfig.rcParams.averageBitRate = bitrate * 1000;
    encodeConfig.rcParams.maxBitRate = bitrate * 1200;
    encodeConfig.rcParams.vbvBufferSize = bitrate * 1000 / fps;  // 1 frame buffer
    encodeConfig.rcParams.vbvInitialDelay = encodeConfig.rcParams.vbvBufferSize;

    // GOP structure - no B frames for low latency
    encodeConfig.gopLength = fps * 2;  // IDR every 2 seconds
    encodeConfig.frameIntervalP = 1;   // No B frames

    // H.264 specific settings
    // idrPeriod should match gopLength for SDK 12+ compatibility
    encodeConfig.encodeCodecConfig.h264Config.idrPeriod = encodeConfig.gopLength;
    // Insert SPS/PPS with every IDR for WebRTC
    encodeConfig.encodeCodecConfig.h264Config.repeatSPSPPS = 1;
    // Use Main profile for better compatibility with P1 preset (Baseline has limitations)
    // WebRTC can decode Main profile - Chrome/Firefox support it
    encodeConfig.profileGUID = NV_ENC_H264_PROFILE_MAIN_GUID;

    LogDebug("[NvencWrapper] Using Main profile, gopLength=%d, idrPeriod=%d",
             encodeConfig.gopLength, encodeConfig.encodeCodecConfig.h264Config.idrPeriod);

    initParams.encodeConfig = &encodeConfig;

    LogDebug("[NvencWrapper] Calling nvEncInitializeEncoder...");
    LogDebug("[NvencWrapper] initParams.version = 0x%x (expected NV_ENC_INITIALIZE_PARAMS_VER = 0x%x)",
             initParams.version, NV_ENC_INITIALIZE_PARAMS_VER);
    LogDebug("[NvencWrapper] Resolution: %dx%d, FPS: %d/%d, Bitrate: %d kbps",
             initParams.encodeWidth, initParams.encodeHeight,
             initParams.frameRateNum, initParams.frameRateDen, bitrate);
    LogDebug("[NvencWrapper] encodeConfig.version = 0x%x (expected NV_ENC_CONFIG_VER = 0x%x)",
             encodeConfig.version, NV_ENC_CONFIG_VER);
    LogDebug("[NvencWrapper] Profile: Baseline, Level: 4, RC: CBR");

    nvStatus = ctx->nvenc.nvEncInitializeEncoder(ctx->encoder, &initParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncInitializeEncoder failed: " + std::to_string(nvStatus);
        LogDebug("[NvencWrapper] nvEncInitializeEncoder FAILED: %d", nvStatus);
        ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }
    LogDebug("[NvencWrapper] nvEncInitializeEncoder succeeded");

    // Create input texture for staging
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

    // Register input resource
    NV_ENC_REGISTER_RESOURCE regRes = {};
    regRes.version = NV_ENC_REGISTER_RESOURCE_VER;
    regRes.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
    regRes.width = width;
    regRes.height = height;
    regRes.pitch = 0;  // D3D11 handles pitch
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

    // Create output bitstream buffer
    NV_ENC_CREATE_BITSTREAM_BUFFER bsParams = {};
    bsParams.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;

    nvStatus = ctx->nvenc.nvEncCreateBitstreamBuffer(ctx->encoder, &bsParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncCreateBitstreamBuffer failed: " + std::to_string(nvStatus);
        ctx->nvenc.nvEncUnregisterResource(ctx->encoder, ctx->registeredResource);
        ctx->inputTexture->Release();
        ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }
    ctx->bitstreamBuffer = bsParams.bitstreamBuffer;

    ctx->initialized = true;
    *outHandle = ctx;

    LogDebug("[NvencWrapper] Encoder created: %dx%d @ %dfps, %dkbps", width, height, fps, bitrate);

    return NVENC_WRAPPER_OK;
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

// Encode texture
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

    // Copy input texture to registered texture
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
    ctx->pts += 10000000 / ctx->fps;  // 100ns units

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

    // Lock and retrieve output
    NV_ENC_LOCK_BITSTREAM lockParams = {};
    lockParams.version = NV_ENC_LOCK_BITSTREAM_VER;
    lockParams.outputBitstream = ctx->bitstreamBuffer;
    lockParams.doNotWait = 0;

    nvStatus = ctx->nvenc.nvEncLockBitstream(ctx->encoder, &lockParams);
    if (nvStatus == NV_ENC_SUCCESS) {
        // Determine if keyframe
        int isKeyFrame = (lockParams.pictureType == NV_ENC_PIC_TYPE_IDR) ? 1 : 0;

        // Also check for SPS/PPS NAL units
        const uint8_t* data = static_cast<const uint8_t*>(lockParams.bitstreamBufferPtr);
        uint32_t size = lockParams.bitstreamSizeInBytes;

        if (!isKeyFrame && size > 5) {
            for (size_t i = 0; i + 4 < size; i++) {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) {
                    int nalType = data[i + 4] & 0x1F;
                    if (nalType == 7 || nalType == 5) {  // SPS or IDR
                        isKeyFrame = 1;
                        break;
                    }
                }
            }
        }

        // Fire callback
        if (ctx->callback) {
            ctx->callback(data, size, lockParams.outputTimeStamp, isKeyFrame, ctx->userData);
        }

        ctx->nvenc.nvEncUnlockBitstream(ctx->encoder, ctx->bitstreamBuffer);
    }

    return NVENC_WRAPPER_OK;
}

// Flush
NVENCWRAPPER_API int NvencFlush(NvencEncoderHandle handle) {
    if (!handle) return NVENC_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<NvencEncoderContext*>(handle);
    if (!ctx->initialized) return NVENC_WRAPPER_NOT_INITIALIZED;

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    // Send EOS
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

            if (ctx->registeredResource) {
                ctx->nvenc.nvEncUnregisterResource(ctx->encoder, ctx->registeredResource);
                ctx->registeredResource = nullptr;
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

    // Use nvEncReconfigureEncoder to change bitrate on the fly
    NV_ENC_RECONFIGURE_PARAMS reconfigParams = {};
    reconfigParams.version = NV_ENC_RECONFIGURE_PARAMS_VER;
    reconfigParams.forceIDR = 1;  // Force IDR after bitrate change

    // Initialize reInitEncodeParams with current settings
    NV_ENC_INITIALIZE_PARAMS& reInitParams = reconfigParams.reInitEncodeParams;
    reInitParams.version = NV_ENC_INITIALIZE_PARAMS_VER;
    reInitParams.encodeGUID = NV_ENC_CODEC_H264_GUID;
    reInitParams.presetGUID = NV_ENC_PRESET_P1_GUID;
    reInitParams.encodeWidth = ctx->width;
    reInitParams.encodeHeight = ctx->height;
    reInitParams.darWidth = ctx->width;
    reInitParams.darHeight = ctx->height;
    reInitParams.frameRateNum = ctx->fps;
    reInitParams.frameRateDen = 1;
    reInitParams.enablePTD = 1;
    reInitParams.maxEncodeWidth = ctx->width;
    reInitParams.maxEncodeHeight = ctx->height;
    reInitParams.tuningInfo = NV_ENC_TUNING_INFO_LOW_LATENCY;

    // Get preset config for the new bitrate
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

    // Update encode config with new bitrate
    NV_ENC_CONFIG encodeConfig = presetConfig.presetCfg;
    encodeConfig.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    encodeConfig.rcParams.averageBitRate = bitrateKbps * 1000;
    encodeConfig.rcParams.maxBitRate = bitrateKbps * 1200;
    encodeConfig.rcParams.vbvBufferSize = bitrateKbps * 1000 / ctx->fps;
    encodeConfig.rcParams.vbvInitialDelay = encodeConfig.rcParams.vbvBufferSize;
    encodeConfig.gopLength = ctx->fps * 2;
    encodeConfig.frameIntervalP = 1;
    encodeConfig.encodeCodecConfig.h264Config.idrPeriod = encodeConfig.gopLength;
    encodeConfig.encodeCodecConfig.h264Config.repeatSPSPPS = 1;
    encodeConfig.profileGUID = NV_ENC_H264_PROFILE_MAIN_GUID;

    reInitParams.encodeConfig = &encodeConfig;

    nvStatus = ctx->nvenc.nvEncReconfigureEncoder(ctx->encoder, &reconfigParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncReconfigureEncoder failed: " + std::to_string(nvStatus);
        LogDebug("[NvencWrapper] SetBitrate failed: nvEncReconfigureEncoder returned %d", nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    ctx->bitrate = bitrateKbps;
    LogDebug("[NvencWrapper] Bitrate changed to %d kbps", bitrateKbps);

    return NVENC_WRAPPER_OK;
}

// Create encoder with BGRA input support (eliminates color conversion)
NVENCWRAPPER_API int NvencCreateEncoderBgra(
    NvencEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate)
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
    ctx->useBgraInput = true;  // Mark as BGRA mode

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

    LogDebug("[NvencWrapper-BGRA] Using SDK 13.0 API, preset P1, BGRA input");

    // Get preset config with tuning info
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

    // Rate control - CBR for streaming
    encodeConfig.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    encodeConfig.rcParams.averageBitRate = bitrate * 1000;
    encodeConfig.rcParams.maxBitRate = bitrate * 1200;
    encodeConfig.rcParams.vbvBufferSize = bitrate * 1000 / fps;
    encodeConfig.rcParams.vbvInitialDelay = encodeConfig.rcParams.vbvBufferSize;

    // GOP structure - no B frames for low latency
    encodeConfig.gopLength = fps * 2;
    encodeConfig.frameIntervalP = 1;

    // H.264 specific settings
    encodeConfig.encodeCodecConfig.h264Config.idrPeriod = encodeConfig.gopLength;
    encodeConfig.encodeCodecConfig.h264Config.repeatSPSPPS = 1;
    encodeConfig.profileGUID = NV_ENC_H264_PROFILE_MAIN_GUID;

    initParams.encodeConfig = &encodeConfig;

    LogDebug("[NvencWrapper-BGRA] Initializing encoder %dx%d @ %dfps, %dkbps", width, height, fps, bitrate);

    nvStatus = ctx->nvenc.nvEncInitializeEncoder(ctx->encoder, &initParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncInitializeEncoder failed: " + std::to_string(nvStatus);
        LogDebug("[NvencWrapper-BGRA] nvEncInitializeEncoder FAILED: %d", nvStatus);
        ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }

    // For BGRA mode, we don't create a staging texture - we'll register input textures on-the-fly
    // or use the provided textures directly

    // Create output bitstream buffer
    NV_ENC_CREATE_BITSTREAM_BUFFER bsParams = {};
    bsParams.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;

    nvStatus = ctx->nvenc.nvEncCreateBitstreamBuffer(ctx->encoder, &bsParams);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncCreateBitstreamBuffer failed: " + std::to_string(nvStatus);
        ctx->nvenc.nvEncDestroyEncoder(ctx->encoder);
        FreeLibrary(ctx->nvencLib);
        delete ctx;
        return NVENC_WRAPPER_FAIL;
    }
    ctx->bitstreamBuffer = bsParams.bitstreamBuffer;

    ctx->initialized = true;
    *outHandle = ctx;

    LogDebug("[NvencWrapper-BGRA] Encoder created: %dx%d @ %dfps, %dkbps (BGRA mode)", width, height, fps, bitrate);

    return NVENC_WRAPPER_OK;
}

// Encode BGRA texture directly (zero-copy, no color conversion)
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

    // Register the BGRA texture directly
    // NV_ENC_BUFFER_FORMAT_ARGB corresponds to DXGI_FORMAT_B8G8R8A8 (word-ordered: BGRA in memory)
    NV_ENC_REGISTER_RESOURCE regRes = {};
    regRes.version = NV_ENC_REGISTER_RESOURCE_VER;
    regRes.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
    regRes.width = ctx->width;
    regRes.height = ctx->height;
    regRes.pitch = 0;
    regRes.resourceToRegister = bgraTexture;
    regRes.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;  // BGRA in memory
    regRes.bufferUsage = NV_ENC_INPUT_IMAGE;

    nvStatus = ctx->nvenc.nvEncRegisterResource(ctx->encoder, &regRes);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncRegisterResource (BGRA) failed: " + std::to_string(nvStatus);
        LogDebug("[NvencWrapper-BGRA] nvEncRegisterResource failed: %d", nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    // Map the registered resource
    NV_ENC_MAP_INPUT_RESOURCE mapRes = {};
    mapRes.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
    mapRes.registeredResource = regRes.registeredResource;

    nvStatus = ctx->nvenc.nvEncMapInputResource(ctx->encoder, &mapRes);
    if (nvStatus != NV_ENC_SUCCESS) {
        g_lastError = "nvEncMapInputResource failed: " + std::to_string(nvStatus);
        ctx->nvenc.nvEncUnregisterResource(ctx->encoder, regRes.registeredResource);
        return NVENC_WRAPPER_FAIL;
    }

    // Encode frame
    NV_ENC_PIC_PARAMS picParams = {};
    picParams.version = NV_ENC_PIC_PARAMS_VER;
    picParams.inputBuffer = mapRes.mappedResource;
    picParams.bufferFmt = NV_ENC_BUFFER_FORMAT_ARGB;  // BGRA format
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

    // Unmap and unregister resource immediately after encode
    ctx->nvenc.nvEncUnmapInputResource(ctx->encoder, mapRes.mappedResource);
    ctx->nvenc.nvEncUnregisterResource(ctx->encoder, regRes.registeredResource);

    if (nvStatus != NV_ENC_SUCCESS && nvStatus != NV_ENC_ERR_NEED_MORE_INPUT) {
        g_lastError = "nvEncEncodePicture failed: " + std::to_string(nvStatus);
        LogDebug("[NvencWrapper-BGRA] nvEncEncodePicture failed: %d", nvStatus);
        return NVENC_WRAPPER_FAIL;
    }

    // Lock and retrieve output
    NV_ENC_LOCK_BITSTREAM lockParams = {};
    lockParams.version = NV_ENC_LOCK_BITSTREAM_VER;
    lockParams.outputBitstream = ctx->bitstreamBuffer;
    lockParams.doNotWait = 0;

    nvStatus = ctx->nvenc.nvEncLockBitstream(ctx->encoder, &lockParams);
    if (nvStatus == NV_ENC_SUCCESS) {
        int isKeyFrame = (lockParams.pictureType == NV_ENC_PIC_TYPE_IDR) ? 1 : 0;

        const uint8_t* data = static_cast<const uint8_t*>(lockParams.bitstreamBufferPtr);
        uint32_t size = lockParams.bitstreamSizeInBytes;

        // Check for SPS/PPS NAL units
        if (!isKeyFrame && size > 5) {
            for (size_t i = 0; i + 4 < size; i++) {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) {
                    int nalType = data[i + 4] & 0x1F;
                    if (nalType == 7 || nalType == 5) {
                        isKeyFrame = 1;
                        break;
                    }
                }
            }
        }

        // Fire callback
        if (ctx->callback) {
            ctx->callback(data, size, lockParams.outputTimeStamp, isKeyFrame, ctx->userData);
        }

        ctx->nvenc.nvEncUnlockBitstream(ctx->encoder, ctx->bitstreamBuffer);
    }

    return NVENC_WRAPPER_OK;
}
