// AmfWrapper.h - AMD AMF SDK Wrapper for C# Interop
// Provides simple C API for zero-copy H.264/H.265 encoding from D3D11 textures

#pragma once

#ifdef AMFWRAPPER_EXPORTS
#define AMFWRAPPER_API __declspec(dllexport)
#else
#define AMFWRAPPER_API __declspec(dllimport)
#endif

#include <stdint.h>
#include <d3d11.h>

#ifdef __cplusplus
extern "C" {
#endif

// Result codes
#define AMF_WRAPPER_OK              0
#define AMF_WRAPPER_FAIL            1
#define AMF_WRAPPER_NOT_INITIALIZED 2
#define AMF_WRAPPER_INVALID_PARAM   3
#define AMF_WRAPPER_NO_OUTPUT       4

// Encoder handle (opaque pointer)
typedef void* AmfEncoderHandle;

// Callback for encoded data
typedef void (*AmfEncodedDataCallback)(
    const uint8_t* data,
    uint32_t size,
    int64_t pts,
    int isKeyFrame,
    void* userData
);

/// <summary>
/// Check if AMF runtime is available
/// </summary>
AMFWRAPPER_API int AmfIsAvailable();

/// <summary>
/// Create an AMF encoder instance (H.264)
/// </summary>
AMFWRAPPER_API int AmfCreateEncoder(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate
);

/// <summary>
/// Set callback for receiving encoded data
/// </summary>
AMFWRAPPER_API int AmfSetEncodedDataCallback(
    AmfEncoderHandle handle,
    AmfEncodedDataCallback callback,
    void* userData
);

/// <summary>
/// Encode a D3D11 NV12 texture (zero-copy)
/// </summary>
AMFWRAPPER_API int AmfEncodeTexture(
    AmfEncoderHandle handle,
    ID3D11Texture2D* texture,
    int forceKeyframe
);

/// <summary>
/// Flush encoder (get remaining frames)
/// </summary>
AMFWRAPPER_API int AmfFlush(AmfEncoderHandle handle);

/// <summary>
/// Encode NV12 data from byte array (uploads to GPU then encodes)
/// </summary>
AMFWRAPPER_API int AmfEncodeNV12Bytes(
    AmfEncoderHandle handle,
    const uint8_t* nv12Data,
    int dataSize,
    int forceKeyframe
);

/// <summary>
/// Destroy encoder and release resources
/// </summary>
AMFWRAPPER_API int AmfDestroyEncoder(AmfEncoderHandle handle);

/// <summary>
/// Get last error message
/// </summary>
AMFWRAPPER_API const char* AmfGetLastError();

/// <summary>
/// Dynamically change encoder bitrate without reinitialization
/// </summary>
AMFWRAPPER_API int AmfSetBitrate(AmfEncoderHandle handle, int bitrateKbps);

/// <summary>
/// Dynamically change encoder FPS without reinitialization
/// </summary>
AMFWRAPPER_API int AmfSetFps(AmfEncoderHandle handle, int fps);

/// <summary>
/// Create an AMF encoder instance with BGRA input (H.264)
/// </summary>
AMFWRAPPER_API int AmfCreateEncoderBgra(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate
);

/// <summary>
/// Encode a D3D11 BGRA texture directly (zero-copy)
/// </summary>
AMFWRAPPER_API int AmfEncodeBgraTexture(
    AmfEncoderHandle handle,
    ID3D11Texture2D* bgraTexture,
    int forceKeyframe
);

// ── H.265/HEVC Extended APIs ──────────────────────────────────────────────

/// <summary>
/// Create an AMF encoder with codec selection (NV12 input)
/// </summary>
/// <param name="useHevc">0 = H.264, 1 = H.265/HEVC</param>
AMFWRAPPER_API int AmfCreateEncoderEx(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate,
    int useHevc
);

/// <summary>
/// Create an AMF encoder with BGRA input and codec selection
/// </summary>
/// <param name="useHevc">0 = H.264, 1 = H.265/HEVC</param>
AMFWRAPPER_API int AmfCreateEncoderBgraEx(
    AmfEncoderHandle* outHandle,
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
