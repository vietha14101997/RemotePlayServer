// NvencWrapper.h - NVIDIA NVENC SDK Wrapper for C# Interop
// Provides simple C API for zero-copy H.264/H.265 encoding from D3D11 textures

#pragma once

#ifdef NVENCWRAPPER_EXPORTS
#define NVENCWRAPPER_API __declspec(dllexport)
#else
#define NVENCWRAPPER_API __declspec(dllimport)
#endif

#include <stdint.h>
#include <d3d11.h>

#ifdef __cplusplus
extern "C" {
#endif

// Result codes
#define NVENC_WRAPPER_OK              0
#define NVENC_WRAPPER_FAIL            1
#define NVENC_WRAPPER_NOT_INITIALIZED 2
#define NVENC_WRAPPER_INVALID_PARAM   3
#define NVENC_WRAPPER_NO_OUTPUT       4

// Encoder handle (opaque pointer)
typedef void* NvencEncoderHandle;

// Callback for encoded data
typedef void (*NvencEncodedDataCallback)(
    const uint8_t* data,
    uint32_t size,
    int64_t pts,
    int isKeyFrame,
    void* userData
);

/// <summary>
/// Check if NVENC runtime is available
/// </summary>
NVENCWRAPPER_API int NvencIsAvailable();

/// <summary>
/// Create an NVENC encoder instance (H.264)
/// </summary>
NVENCWRAPPER_API int NvencCreateEncoder(
    NvencEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate
);

/// <summary>
/// Set callback for receiving encoded data
/// </summary>
NVENCWRAPPER_API int NvencSetEncodedDataCallback(
    NvencEncoderHandle handle,
    NvencEncodedDataCallback callback,
    void* userData
);

/// <summary>
/// Encode a D3D11 NV12 texture (zero-copy)
/// </summary>
NVENCWRAPPER_API int NvencEncodeTexture(
    NvencEncoderHandle handle,
    ID3D11Texture2D* texture,
    int forceKeyframe
);

/// <summary>
/// Flush encoder (get remaining frames)
/// </summary>
NVENCWRAPPER_API int NvencFlush(NvencEncoderHandle handle);

/// <summary>
/// Destroy encoder and release resources
/// </summary>
NVENCWRAPPER_API int NvencDestroyEncoder(NvencEncoderHandle handle);

/// <summary>
/// Get last error message
/// </summary>
NVENCWRAPPER_API const char* NvencGetLastError();

/// <summary>
/// Dynamically change encoder bitrate without reinitialization
/// </summary>
NVENCWRAPPER_API int NvencSetBitrate(NvencEncoderHandle handle, int bitrateKbps);

/// <summary>
/// Dynamically change encoder FPS without reinitialization
/// </summary>
NVENCWRAPPER_API int NvencSetFps(NvencEncoderHandle handle, int fps);

/// <summary>
/// Create an NVENC encoder instance with BGRA input (H.264)
/// </summary>
NVENCWRAPPER_API int NvencCreateEncoderBgra(
    NvencEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate
);

/// <summary>
/// Encode a D3D11 BGRA texture directly (zero-copy)
/// </summary>
NVENCWRAPPER_API int NvencEncodeBgraTexture(
    NvencEncoderHandle handle,
    ID3D11Texture2D* bgraTexture,
    int forceKeyframe
);

// ── H.265/HEVC Extended APIs ──────────────────────────────────────────────

/// <summary>
/// Create an NVENC encoder instance with codec selection (H.264 or H.265)
/// </summary>
/// <param name="useHevc">0 = H.264, 1 = H.265/HEVC</param>
NVENCWRAPPER_API int NvencCreateEncoderEx(
    NvencEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate,
    int useHevc
);

/// <summary>
/// Create an NVENC encoder with BGRA input and codec selection
/// </summary>
/// <param name="useHevc">0 = H.264, 1 = H.265/HEVC</param>
NVENCWRAPPER_API int NvencCreateEncoderBgraEx(
    NvencEncoderHandle* outHandle,
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
