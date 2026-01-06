// QsvWrapper.cpp - Intel Quick Sync Video Wrapper Implementation
// Uses Media Foundation Hardware MFT for H.264 encoding with Intel QSV acceleration

#include "QsvWrapper.h"

#include <string>
#include <mutex>
#include <atomic>
#include <vector>
#include <fstream>

// Windows and Media Foundation headers
#include <windows.h>
#include <initguid.h>
#include <dxgi.h>
#include <d3d11.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <strmif.h>
#include <wmcodecdsp.h>
#include <codecapi.h>

#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dxguid.lib")
#pragma comment(lib, "strmiids.lib")
#pragma comment(lib, "wmcodecdspuuid.lib")

// Debug logging
static std::ofstream g_logFile;
static std::mutex g_logMutex;

static void LogDebug(const char* format, ...) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (!g_logFile.is_open()) {
        g_logFile.open("logs/qsv_debug.log", std::ios::out | std::ios::trunc);
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

// Helper to check HRESULT
#define CHECK_HR(hr, msg) \
    if (FAILED(hr)) { \
        g_lastError = std::string(msg) + ": " + std::to_string(hr); \
        LogDebug("[QsvWrapper] %s", g_lastError.c_str()); \
        return QSV_WRAPPER_FAIL; \
    }

// Safe release template
template<class T>
void SafeRelease(T** ppT) {
    if (*ppT) {
        (*ppT)->Release();
        *ppT = nullptr;
    }
}

// Encoder context structure
struct QsvEncoderContext {
    // Media Foundation
    IMFTransform* encoder = nullptr;
    IMFDXGIDeviceManager* deviceManager = nullptr;
    UINT deviceResetToken = 0;

    // D3D11 resources
    ID3D11Device* d3dDevice = nullptr;
    ID3D11DeviceContext* d3dContext = nullptr;
    ID3D11Texture2D* stagingTexture = nullptr;

    // Encoder settings
    int width = 0;
    int height = 0;
    int fps = 0;
    int bitrate = 0;

    // Callback
    QsvEncodedDataCallback callback = nullptr;
    void* userData = nullptr;

    // State
    int64_t pts = 0;
    int64_t frameIndex = 0;
    std::atomic<bool> initialized{ false };
    std::mutex encodeMutex;

    // Output buffer
    std::vector<uint8_t> outputBuffer;
};

// Find Intel QSV hardware encoder MFT
static HRESULT FindQsvEncoder(IMFTransform** ppEncoder, IMFDXGIDeviceManager* deviceManager) {
    HRESULT hr = S_OK;
    IMFActivate** ppActivate = nullptr;
    UINT32 count = 0;

    // Enumerate hardware H.264 encoders
    MFT_REGISTER_TYPE_INFO inputType = { MFMediaType_Video, MFVideoFormat_NV12 };
    MFT_REGISTER_TYPE_INFO outputType = { MFMediaType_Video, MFVideoFormat_H264 };

    hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        &inputType,
        &outputType,
        &ppActivate,
        &count
    );

    if (FAILED(hr) || count == 0) {
        g_lastError = "No hardware H.264 encoder found";
        return E_FAIL;
    }

    // Try to find Intel QSV encoder (or any hardware encoder)
    bool found = false;
    for (UINT32 i = 0; i < count && !found; i++) {
        WCHAR* friendlyName = nullptr;
        UINT32 nameLen = 0;
        ppActivate[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendlyName, &nameLen);

        LogDebug("[QsvWrapper] Found encoder: %ls", friendlyName ? friendlyName : L"Unknown");

        // Try to activate this encoder
        hr = ppActivate[i]->ActivateObject(IID_PPV_ARGS(ppEncoder));
        if (SUCCEEDED(hr)) {
            // Set D3D11 device manager for hardware acceleration
            if (deviceManager) {
                hr = (*ppEncoder)->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)deviceManager);
                if (FAILED(hr)) {
                    LogDebug("[QsvWrapper] Failed to set D3D manager for %ls", friendlyName);
                    (*ppEncoder)->Release();
                    *ppEncoder = nullptr;
                }
                else {
                    found = true;
                    LogDebug("[QsvWrapper] Using encoder: %ls", friendlyName);
                }
            }
            else {
                found = true;
            }
        }

        if (friendlyName) CoTaskMemFree(friendlyName);
    }

    // Cleanup
    for (UINT32 i = 0; i < count; i++) {
        ppActivate[i]->Release();
    }
    CoTaskMemFree(ppActivate);

    return found ? S_OK : E_FAIL;
}

// Check QSV availability
QSVWRAPPER_API int QsvIsAvailable() {
    // Check if Intel integrated graphics is present
    IDXGIFactory1* factory = nullptr;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return 0;

    bool foundIntel = false;
    IDXGIAdapter1* adapter = nullptr;
    for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
        DXGI_ADAPTER_DESC1 desc;
        adapter->GetDesc1(&desc);

        // Intel vendor ID
        if (desc.VendorId == 0x8086) {
            foundIntel = true;
            adapter->Release();
            break;
        }
        adapter->Release();
    }
    factory->Release();

    if (!foundIntel) return 0;

    // Also check if MF hardware encoder is available
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) return 0;

    IMFActivate** ppActivate = nullptr;
    UINT32 count = 0;
    MFT_REGISTER_TYPE_INFO inputType = { MFMediaType_Video, MFVideoFormat_NV12 };
    MFT_REGISTER_TYPE_INFO outputType = { MFMediaType_Video, MFVideoFormat_H264 };

    hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        MFT_ENUM_FLAG_HARDWARE,
        &inputType,
        &outputType,
        &ppActivate,
        &count
    );

    if (SUCCEEDED(hr) && count > 0) {
        for (UINT32 i = 0; i < count; i++) {
            ppActivate[i]->Release();
        }
        CoTaskMemFree(ppActivate);
    }

    MFShutdown();

    return (count > 0) ? 1 : 0;
}

// Create encoder
QSVWRAPPER_API int QsvCreateEncoder(
    QsvEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate)
{
    if (!outHandle || !d3d11Device || width <= 0 || height <= 0) {
        g_lastError = "Invalid parameters";
        return QSV_WRAPPER_INVALID_PARAM;
    }

    *outHandle = nullptr;

    HRESULT hr;

    // Initialize Media Foundation
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) {
        g_lastError = "MFStartup failed";
        return QSV_WRAPPER_FAIL;
    }

    auto ctx = new QsvEncoderContext();
    ctx->d3dDevice = d3d11Device;
    d3d11Device->AddRef();
    d3d11Device->GetImmediateContext(&ctx->d3dContext);
    ctx->width = width;
    ctx->height = height;
    ctx->fps = fps;
    ctx->bitrate = bitrate;

    // Create DXGI device manager
    hr = MFCreateDXGIDeviceManager(&ctx->deviceResetToken, &ctx->deviceManager);
    CHECK_HR(hr, "MFCreateDXGIDeviceManager failed");

    hr = ctx->deviceManager->ResetDevice(d3d11Device, ctx->deviceResetToken);
    CHECK_HR(hr, "ResetDevice failed");

    // Find and create hardware encoder
    hr = FindQsvEncoder(&ctx->encoder, ctx->deviceManager);
    if (FAILED(hr)) {
        g_lastError = "Failed to find Intel QSV encoder";
        SafeRelease(&ctx->deviceManager);
        ctx->d3dDevice->Release();
        ctx->d3dContext->Release();
        delete ctx;
        MFShutdown();
        return QSV_WRAPPER_FAIL;
    }

    // Set output media type (H.264)
    IMFMediaType* outputType = nullptr;
    hr = MFCreateMediaType(&outputType);
    CHECK_HR(hr, "MFCreateMediaType output failed");

    hr = outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    hr = outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    hr = outputType->SetUINT32(MF_MT_AVG_BITRATE, bitrate * 1000);
    hr = MFSetAttributeSize(outputType, MF_MT_FRAME_SIZE, width, height);
    hr = MFSetAttributeRatio(outputType, MF_MT_FRAME_RATE, fps, 1);
    hr = outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    // Baseline profile for WebRTC compatibility
    hr = outputType->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
    hr = outputType->SetUINT32(MF_MT_MPEG2_LEVEL, eAVEncH264VLevel4);

    hr = ctx->encoder->SetOutputType(0, outputType, 0);
    SafeRelease(&outputType);
    CHECK_HR(hr, "SetOutputType failed");

    // Set input media type (NV12)
    IMFMediaType* inputType = nullptr;
    hr = MFCreateMediaType(&inputType);
    CHECK_HR(hr, "MFCreateMediaType input failed");

    hr = inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    hr = inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    hr = MFSetAttributeSize(inputType, MF_MT_FRAME_SIZE, width, height);
    hr = MFSetAttributeRatio(inputType, MF_MT_FRAME_RATE, fps, 1);
    hr = inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

    hr = ctx->encoder->SetInputType(0, inputType, 0);
    SafeRelease(&inputType);
    CHECK_HR(hr, "SetInputType failed");

    // Configure encoder for low latency
    ICodecAPI* codecApi = nullptr;
    hr = ctx->encoder->QueryInterface(IID_PPV_ARGS(&codecApi));
    if (SUCCEEDED(hr)) {
        VARIANT var;
        VariantInit(&var);

        // Low latency mode
        var.vt = VT_BOOL;
        var.boolVal = VARIANT_TRUE;
        codecApi->SetValue(&CODECAPI_AVLowLatencyMode, &var);

        // CBR rate control
        var.vt = VT_UI4;
        var.ulVal = eAVEncCommonRateControlMode_CBR;
        codecApi->SetValue(&CODECAPI_AVEncCommonRateControlMode, &var);

        // GOP size (2 seconds)
        var.vt = VT_UI4;
        var.ulVal = fps * 2;
        codecApi->SetValue(&CODECAPI_AVEncMPVGOPSize, &var);

        codecApi->Release();
    }

    // Start encoder
    hr = ctx->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    CHECK_HR(hr, "BEGIN_STREAMING failed");

    hr = ctx->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    CHECK_HR(hr, "START_OF_STREAM failed");

    // Create staging texture
    D3D11_TEXTURE2D_DESC texDesc = {};
    texDesc.Width = width;
    texDesc.Height = height;
    texDesc.MipLevels = 1;
    texDesc.ArraySize = 1;
    texDesc.Format = DXGI_FORMAT_NV12;
    texDesc.SampleDesc.Count = 1;
    texDesc.Usage = D3D11_USAGE_DEFAULT;
    texDesc.BindFlags = 0;

    hr = d3d11Device->CreateTexture2D(&texDesc, nullptr, &ctx->stagingTexture);
    CHECK_HR(hr, "CreateTexture2D staging failed");

    ctx->outputBuffer.resize(width * height * 2);  // Max output size
    ctx->initialized = true;
    *outHandle = ctx;

    LogDebug("[QsvWrapper] Encoder created: %dx%d @ %dfps, %dkbps", width, height, fps, bitrate);

    return QSV_WRAPPER_OK;
}

// Set callback
QSVWRAPPER_API int QsvSetEncodedDataCallback(
    QsvEncoderHandle handle,
    QsvEncodedDataCallback callback,
    void* userData)
{
    if (!handle) return QSV_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<QsvEncoderContext*>(handle);
    ctx->callback = callback;
    ctx->userData = userData;

    return QSV_WRAPPER_OK;
}

// Encode texture
QSVWRAPPER_API int QsvEncodeTexture(QsvEncoderHandle handle, ID3D11Texture2D* nv12Texture, int forceKeyframe)
{
    if (!handle || !nv12Texture) {
        g_lastError = "Invalid parameters";
        return QSV_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<QsvEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return QSV_WRAPPER_NOT_INITIALIZED;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    HRESULT hr;

    // Copy input texture to staging
    ctx->d3dContext->CopyResource(ctx->stagingTexture, nv12Texture);

    // Create input sample from texture
    IMFSample* inputSample = nullptr;
    IMFMediaBuffer* inputBuffer = nullptr;

    hr = MFCreateDXGISurfaceBuffer(IID_ID3D11Texture2D, ctx->stagingTexture, 0, FALSE, &inputBuffer);
    if (FAILED(hr)) {
        g_lastError = "MFCreateDXGISurfaceBuffer failed";
        return QSV_WRAPPER_FAIL;
    }

    hr = MFCreateSample(&inputSample);
    if (FAILED(hr)) {
        inputBuffer->Release();
        g_lastError = "MFCreateSample failed";
        return QSV_WRAPPER_FAIL;
    }

    hr = inputSample->AddBuffer(inputBuffer);
    inputBuffer->Release();

    // Set sample timestamp
    LONGLONG sampleTime = ctx->pts;
    ctx->pts += 10000000 / ctx->fps;  // 100ns units
    inputSample->SetSampleTime(sampleTime);
    inputSample->SetSampleDuration(10000000 / ctx->fps);

    // Force keyframe if requested
    if (forceKeyframe) {
        ICodecAPI* codecApi = nullptr;
        hr = ctx->encoder->QueryInterface(IID_PPV_ARGS(&codecApi));
        if (SUCCEEDED(hr)) {
            VARIANT var;
            VariantInit(&var);
            var.vt = VT_UI4;
            var.ulVal = 1;
            codecApi->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &var);
            codecApi->Release();
        }
    }

    // Process input
    hr = ctx->encoder->ProcessInput(0, inputSample, 0);
    inputSample->Release();

    if (FAILED(hr)) {
        g_lastError = "ProcessInput failed: " + std::to_string(hr);
        return QSV_WRAPPER_FAIL;
    }

    // Get output
    MFT_OUTPUT_DATA_BUFFER outputData = {};
    DWORD status = 0;

    // Create output sample
    IMFSample* outputSample = nullptr;
    IMFMediaBuffer* outputBuffer = nullptr;

    hr = MFCreateMemoryBuffer(static_cast<DWORD>(ctx->outputBuffer.size()), &outputBuffer);
    if (FAILED(hr)) return QSV_WRAPPER_FAIL;

    hr = MFCreateSample(&outputSample);
    if (FAILED(hr)) {
        outputBuffer->Release();
        return QSV_WRAPPER_FAIL;
    }

    outputSample->AddBuffer(outputBuffer);
    outputData.pSample = outputSample;

    hr = ctx->encoder->ProcessOutput(0, 1, &outputData, &status);

    if (SUCCEEDED(hr)) {
        // Get encoded data
        BYTE* data = nullptr;
        DWORD dataLen = 0;
        hr = outputBuffer->Lock(&data, nullptr, &dataLen);

        if (SUCCEEDED(hr) && dataLen > 0) {
            // Check for keyframe (SPS or IDR NAL)
            int isKeyFrame = 0;
            for (DWORD i = 0; i + 4 < dataLen; i++) {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) {
                    int nalType = data[i + 4] & 0x1F;
                    if (nalType == 7 || nalType == 5) {
                        isKeyFrame = 1;
                        break;
                    }
                }
            }

            // Fire callback
            if (ctx->callback) {
                ctx->callback(data, dataLen, sampleTime, isKeyFrame, ctx->userData);
            }

            outputBuffer->Unlock();
        }
    }

    SafeRelease(&outputBuffer);
    SafeRelease(&outputSample);

    if (outputData.pEvents) {
        outputData.pEvents->Release();
    }

    ctx->frameIndex++;

    return QSV_WRAPPER_OK;
}

// Flush
QSVWRAPPER_API int QsvFlush(QsvEncoderHandle handle) {
    if (!handle) return QSV_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<QsvEncoderContext*>(handle);
    if (!ctx->initialized) return QSV_WRAPPER_NOT_INITIALIZED;

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    // Send drain command
    ctx->encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);

    return QSV_WRAPPER_OK;
}

// Destroy
QSVWRAPPER_API int QsvDestroyEncoder(QsvEncoderHandle handle) {
    if (!handle) return QSV_WRAPPER_INVALID_PARAM;

    auto ctx = static_cast<QsvEncoderContext*>(handle);

    {
        std::lock_guard<std::mutex> lock(ctx->encodeMutex);
        ctx->initialized = false;

        if (ctx->encoder) {
            ctx->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            ctx->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            ctx->encoder->Release();
            ctx->encoder = nullptr;
        }

        SafeRelease(&ctx->stagingTexture);
        SafeRelease(&ctx->deviceManager);

        if (ctx->d3dContext) {
            ctx->d3dContext->Release();
            ctx->d3dContext = nullptr;
        }

        if (ctx->d3dDevice) {
            ctx->d3dDevice->Release();
            ctx->d3dDevice = nullptr;
        }
    }

    delete ctx;
    MFShutdown();

    return QSV_WRAPPER_OK;
}

// Dynamically change encoder bitrate
QSVWRAPPER_API int QsvSetBitrate(QsvEncoderHandle handle, int bitrateKbps) {
    if (!handle) {
        g_lastError = "Invalid handle";
        return QSV_WRAPPER_INVALID_PARAM;
    }

    if (bitrateKbps <= 0) {
        g_lastError = "Invalid bitrate";
        return QSV_WRAPPER_INVALID_PARAM;
    }

    auto ctx = static_cast<QsvEncoderContext*>(handle);
    if (!ctx->initialized) {
        g_lastError = "Encoder not initialized";
        return QSV_WRAPPER_NOT_INITIALIZED;
    }

    std::lock_guard<std::mutex> lock(ctx->encodeMutex);

    HRESULT hr;
    ICodecAPI* codecApi = nullptr;
    hr = ctx->encoder->QueryInterface(IID_PPV_ARGS(&codecApi));
    if (FAILED(hr)) {
        g_lastError = "Failed to get ICodecAPI";
        return QSV_WRAPPER_FAIL;
    }

    VARIANT var;
    VariantInit(&var);

    // Set new mean bitrate (in bits/s)
    var.vt = VT_UI4;
    var.ulVal = bitrateKbps * 1000;
    hr = codecApi->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &var);
    if (FAILED(hr)) {
        // Log but continue - some encoders may not support dynamic bitrate
        LogDebug("[QsvSetBitrate] CODECAPI_AVEncCommonMeanBitRate failed: 0x%08X, trying MaxBitRate", hr);
        // Try max bitrate as fallback
        hr = codecApi->SetValue(&CODECAPI_AVEncCommonMaxBitRate, &var);
        if (FAILED(hr)) {
            codecApi->Release();
            g_lastError = "SetValue bitrate failed: " + std::to_string(hr);
            return QSV_WRAPPER_FAIL;
        }
    }

    // Force keyframe after bitrate change
    var.vt = VT_UI4;
    var.ulVal = 1;
    codecApi->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &var);

    codecApi->Release();

    ctx->bitrate = bitrateKbps;
    LogDebug("[QsvSetBitrate] Bitrate changed to %d kbps", bitrateKbps);

    return QSV_WRAPPER_OK;
}

// Get last error
QSVWRAPPER_API const char* QsvGetLastError() {
    return g_lastError.c_str();
}
