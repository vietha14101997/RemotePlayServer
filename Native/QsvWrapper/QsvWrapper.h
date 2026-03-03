// QsvWrapper.h - Intel Quick Sync Video Wrapper for C# Interop
// Provides simple C API for zero-copy H.264/H.265 encoding from D3D11 textures

#pragma once

#ifdef QSVWRAPPER_EXPORTS
#define QSVWRAPPER_API __declspec(dllexport)
#else
#define QSVWRAPPER_API __declspec(dllimport)
#endif

#include <stdint.h>
#include <d3d11.h>

#ifdef __cplusplus
extern "C" {
#endif

// Result codes
#define QSV_WRAPPER_OK              0
#define QSV_WRAPPER_FAIL            1
#define QSV_WRAPPER_NOT_INITIALIZED 2
#define QSV_WRAPPER_INVALID_PARAM   3
#define QSV_WRAPPER_NO_OUTPUT       4

// Encoder handle (opaque pointer)
typedef void* QsvEncoderHandle;

// Callback for encoded data
typedef void (*QsvEncodedDataCallback)(
    const uint8_t* data,
    uint32_t size,
    int64_t pts,
    int isKeyFrame,
    void* userData
);

/// <summary>
/// Check if Intel QSV is available
/// </summary>
QSVWRAPPER_API int QsvIsAvailable();

/// <summary>
/// Create a QSV encoder instance (H.264)
/// </summary>
QSVWRAPPER_API int QsvCreateEncoder(
    QsvEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate
);

/// <summary>
/// Set callback for receiving encoded data
/// </summary>
QSVWRAPPER_API int QsvSetEncodedDataCallback(
    QsvEncoderHandle handle,
    QsvEncodedDataCallback callback,
    void* userData
);

/// <summary>
/// Encode a D3D11 NV12 texture (zero-copy)
/// </summary>
QSVWRAPPER_API int QsvEncodeTexture(
    QsvEncoderHandle handle,
    ID3D11Texture2D* texture,
    int forceKeyframe
);

/// <summary>
/// Flush encoder (get remaining frames)
/// </summary>
QSVWRAPPER_API int QsvFlush(QsvEncoderHandle handle);

/// <summary>
/// Destroy encoder and release resources
/// </summary>
QSVWRAPPER_API int QsvDestroyEncoder(QsvEncoderHandle handle);

/// <summary>
/// Get last error message
/// </summary>
QSVWRAPPER_API const char* QsvGetLastError();

/// <summary>
/// Dynamically change encoder bitrate without reinitialization
/// </summary>
QSVWRAPPER_API int QsvSetBitrate(QsvEncoderHandle handle, int bitrateKbps);

/// <summary>
/// Dynamically change encoder FPS without reinitialization
/// NOTE: QSV does not support runtime FPS changes, this always returns QSV_WRAPPER_FAIL
/// </summary>
QSVWRAPPER_API int QsvSetFps(QsvEncoderHandle handle, int fps);

// ── H.265/HEVC Extended APIs ──────────────────────────────────────────────

/// <summary>
/// Create a QSV encoder with codec selection
/// </summary>
/// <param name="useHevc">0 = H.264, 1 = H.265/HEVC</param>
QSVWRAPPER_API int QsvCreateEncoderEx(
    QsvEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate,
    int useHevc
);

#ifdef __cplusplus
}
#endif
