// QsvWrapper.cpp - Intel Quick Sync Video Wrapper Implementation
// Uses Media Foundation Hardware MFT for H.264/H.265 encoding with Intel QSV acceleration

#include "QsvWrapper.h"

#include <string>
#include <mutex>
#include <atomic>
#include <vector>

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

// Thread-safe error message
static thread_local std::string g_lastError;

// Helper to check HRESULT
#define CHECK_HR(hr, msg) \
    if (FAILED(hr)) { \
        g_lastError = std::string(msg) + ": " + std::to_string(hr); \
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

    // Cached ICodecAPI to avoid repeated QueryInterface
    ICodecAPI* codecApi = nullptr;

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

    bool useHevc = false;

    // Output buffer
    std::vector<uint8_t> outputBuffer;
};

// Find Intel QSV hardware encoder MFT (codec-aware)
static HRESULT FindQsvEncoder(IMFTransform** ppEncoder, IMFDXGIDeviceManager* deviceManager, bool useHevc) {
    HRESULT hr = S_OK;
    IMFActivate** ppActivate = nullptr;
    UINT32 count = 0;

    GUID outputSubtype = useHevc ? MFVideoFormat_HEVC : MFVideoFormat_H264;

    MFT_REGISTER_TYPE_INFO inputType = { MFMediaType_Video, MFVideoFormat_NV12 };
    MFT_REGISTER_TYPE_INFO outputType = { MFMediaType_Video, outputSubtype };

    hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        &inputType,
        &outputType,
        &ppActivate,
        &count
    );

    if (FAILED(hr) || count == 0) {
        g_lastError = useHevc ? "No hardware H.265 encoder found" : "No hardware H.264 encoder found";
        return E_FAIL;
    }

    bool found = false;
    for (UINT32 i = 0; i < count && !found; i++) {
        hr = ppActivate[i]->ActivateObject(IID_PPV_ARGS(ppEncoder));
        if (SUCCEEDED(hr)) {
            if (deviceManager) {
                hr = (*ppEncoder)->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)deviceManager);
                if (FAILED(hr)) {
                    (*ppEncoder)->Release();
                    *ppEncoder = nullptr;
                } else {
                    found = true;
                }
            } else {
                found = true;
            }
        }
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
    IDXGIFactory1* factory = nullptr;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return 0;

    bool foundIntel = false;
    IDXGIAdapter1* adapter = nullptr;
    for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
        DXGI_ADAPTER_DESC1 desc;
        adapter->GetDesc1(&desc);
        if (desc.VendorId == 0x8086) {
            foundIntel = true;
            adapter->Release();
            break;
        }
        adapter->Release();
    }
    factory->Release();

    if (!foundIntel) return 0;

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

// Internal: Create encoder with codec selection
static int QsvCreateEncoderInternal(
    QsvEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate,
    bool useHevc)
{
    if (!outHandle || !d3d11Device || width <= 0 || height <= 0) {
        g_lastError = "Invalid parameters";
        return QSV_WRAPPER_INVALID_PARAM;
    }

    *outHandle = nullptr;

    HRESULT hr;

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
    ctx->useHevc = useHevc;

    // Create DXGI device manager
    hr = MFCreateDXGIDeviceManager(&ctx->deviceResetToken, &ctx->deviceManager);
    CHECK_HR(hr, "MFCreateDXGIDeviceManager failed");

    hr = ctx->deviceManager->ResetDevice(d3d11Device, ctx->deviceResetToken);
    CHECK_HR(hr, "ResetDevice failed");

    // Find and create hardware encoder (codec-aware)
    hr = FindQsvEncoder(&ctx->encoder, ctx->deviceManager, useHevc);
    if (FAILED(hr)) {
        g_lastError = useHevc ? "Failed to find Intel QSV HEVC encoder" : "Failed to find Intel QSV encoder";
        SafeRelease(&ctx->deviceManager);
        ctx->d3dDevice->Release();
        ctx->d3dContext->Release();
        delete ctx;
        MFShutdown();
        return QSV_WRAPPER_FAIL;
    }

    // Set output media type (H.264 or H.265)
    GUID outputSubtype = useHevc ? MFVideoFormat_HEVC : MFVideoFormat_H264;

    IMFMediaType* outputMediaType = nullptr;
    hr = MFCreateMediaType(&outputMediaType);
    CHECK_HR(hr, "MFCreateMediaType output failed");

    hr = outputMediaType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    hr = outputMediaType->SetGUID(MF_MT_SUBTYPE, outputSubtype);
    hr = outputMediaType->SetUINT32(MF_MT_AVG_BITRATE, bitrate * 1000);
    hr = MFSetAttributeSize(outputMediaType, MF_MT_FRAME_SIZE, width, height);
    hr = MFSetAttributeRatio(outputMediaType, MF_MT_FRAME_RATE, fps, 1);
    hr = outputMediaType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

    if (useHevc) {
        hr = outputMediaType->SetUINT32(MF_MT_MPEG2_PROFILE, 1);  // Main profile for HEVC
    } else {
        hr = outputMediaType->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
        hr = outputMediaType->SetUINT32(MF_MT_MPEG2_LEVEL, eAVEncH264VLevel4);
    }

    hr = ctx->encoder->SetOutputType(0, outputMediaType, 0);
    SafeRelease(&outputMediaType);
    CHECK_HR(hr, "SetOutputType failed");

    // Set input media type (NV12)
    IMFMediaType* inputMediaType = nullptr;
    hr = MFCreateMediaType(&inputMediaType);
    CHECK_HR(hr, "MFCreateMediaType input failed");

    hr = inputMediaType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    hr = inputMediaType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    hr = MFSetAttributeSize(inputMediaType, MF_MT_FRAME_SIZE, width, height);
    hr = MFSetAttributeRatio(inputMediaType, MF_MT_FRAME_RATE, fps, 1);
    hr = inputMediaType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

    hr = ctx->encoder->SetInputType(0, inputMediaType, 0);
    SafeRelease(&inputMediaType);
    CHECK_HR(hr, "SetInputType failed");

    // Cache ICodecAPI and configure for low latency
    hr = ctx->encoder->QueryInterface(IID_PPV_ARGS(&ctx->codecApi));
    if (SUCCEEDED(hr)) {
        VARIANT var;
        VariantInit(&var);

        var.vt = VT_BOOL;
        var.boolVal = VARIANT_TRUE;
        ctx->codecApi->SetValue(&CODECAPI_AVLowLatencyMode, &var);

        var.vt = VT_UI4;
        var.ulVal = eAVEncCommonRateControlMode_CBR;
        ctx->codecApi->SetValue(&CODECAPI_AVEncCommonRateControlMode, &var);

        var.vt = VT_UI4;
        var.ulVal = 0; // Infinite GOP (match Nvenc)
        ctx->codecApi->SetValue(&CODECAPI_AVEncMPVGOPSize, &var);
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

    ctx->outputBuffer.resize(width * height * 2);
    ctx->initialized = true;
    *outHandle = ctx;

    return QSV_WRAPPER_OK;
}

// Create encoder (H.264)
QSVWRAPPER_API int QsvCreateEncoder(
    QsvEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate)
{
    return QsvCreateEncoderInternal(outHandle, d3d11Device, width, height, fps, bitrate, false);
}

// Create encoder with codec selection
QSVWRAPPER_API int QsvCreateEncoderEx(
    QsvEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width, int height, int fps, int bitrate, int useHevc)
{
    return QsvCreateEncoderInternal(outHandle, d3d11Device, width, height, fps, bitrate, useHevc != 0);
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

// Encode texture (codec-aware keyframe detection)
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
    ctx->pts += 10000000 / ctx->fps;
    inputSample->SetSampleTime(sampleTime);
    inputSample->SetSampleDuration(10000000 / ctx->fps);

    // Force keyframe if requested (use cached codecApi)
    if (forceKeyframe && ctx->codecApi) {
        VARIANT var;
        VariantInit(&var);
        var.vt = VT_UI4;
        var.ulVal = 1;
        ctx->codecApi->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &var);
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
        BYTE* data = nullptr;
        DWORD dataLen = 0;
        hr = outputBuffer->Lock(&data, nullptr, &dataLen);

        if (SUCCEEDED(hr) && dataLen > 0) {
            int isKeyFrame = ctx->useHevc ? DetectKeyframeHEVC(data, dataLen) : DetectKeyframeH264(data, dataLen);

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

        // Release cached ICodecAPI
        if (ctx->codecApi) {
            ctx->codecApi->Release();
            ctx->codecApi = nullptr;
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

    if (!ctx->codecApi) {
        g_lastError = "ICodecAPI not available";
        return QSV_WRAPPER_FAIL;
    }

    VARIANT var;
    VariantInit(&var);

    // Set new mean bitrate (in bits/s)
    var.vt = VT_UI4;
    var.ulVal = bitrateKbps * 1000;
    HRESULT hr = ctx->codecApi->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &var);
    if (FAILED(hr)) {
        // Try max bitrate as fallback
        hr = ctx->codecApi->SetValue(&CODECAPI_AVEncCommonMaxBitRate, &var);
        if (FAILED(hr)) {
            g_lastError = "SetValue bitrate failed: " + std::to_string(hr);
            return QSV_WRAPPER_FAIL;
        }
    }

    // Force keyframe after bitrate change
    var.vt = VT_UI4;
    var.ulVal = 1;
    ctx->codecApi->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &var);

    ctx->bitrate = bitrateKbps;

    return QSV_WRAPPER_OK;
}

// Dynamically change encoder FPS - NOT SUPPORTED by QSV/Media Foundation
QSVWRAPPER_API int QsvSetFps(QsvEncoderHandle handle, int fps) {
    (void)handle;
    (void)fps;

    g_lastError = "Runtime FPS change not supported by QSV encoder";
    return QSV_WRAPPER_FAIL;
}

// Get last error
QSVWRAPPER_API const char* QsvGetLastError() {
    return g_lastError.c_str();
}
