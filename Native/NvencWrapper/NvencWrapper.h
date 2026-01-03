// NvencWrapper.h - NVIDIA NVENC SDK Wrapper for C# Interop
// Provides simple C API for zero-copy H.264 encoding from D3D11 textures

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
/// Create an NVENC encoder instance
/// </summary>
/// <param name="outHandle">Output encoder handle</param>
/// <param name="d3d11Device">D3D11 device to use for encoding (shared device)</param>
/// <param name="width">Video width</param>
/// <param name="height">Video height</param>
/// <param name="fps">Frames per second</param>
/// <param name="bitrate">Target bitrate in kbps</param>
/// <returns>NVENC_WRAPPER_OK on success</returns>
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
/// <param name="handle">Encoder handle</param>
/// <param name="texture">D3D11 NV12 texture to encode</param>
/// <param name="forceKeyframe">Force IDR frame</param>
/// <returns>NVENC_WRAPPER_OK on success</returns>
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

#ifdef __cplusplus
}
#endif
