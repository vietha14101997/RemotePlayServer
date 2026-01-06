// AmfWrapper.h - AMD AMF SDK Wrapper for C# Interop
// Provides simple C API for zero-copy H.264 encoding from D3D11 textures

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
/// Create an AMF encoder instance
/// </summary>
/// <param name="outHandle">Output encoder handle</param>
/// <param name="d3d11Device">D3D11 device to use for encoding (shared device)</param>
/// <param name="width">Video width</param>
/// <param name="height">Video height</param>
/// <param name="fps">Frames per second</param>
/// <param name="bitrate">Target bitrate in kbps</param>
/// <returns>AMF_WRAPPER_OK on success</returns>
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
/// <param name="handle">Encoder handle</param>
/// <param name="texture">D3D11 NV12 texture to encode</param>
/// <param name="forceKeyframe">Force IDR frame</param>
/// <returns>AMF_WRAPPER_OK on success</returns>
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
/// <param name="handle">Encoder handle</param>
/// <param name="nv12Data">NV12 pixel data (Y plane followed by interleaved UV plane)</param>
/// <param name="dataSize">Size of nv12Data in bytes</param>
/// <param name="forceKeyframe">Force IDR frame</param>
/// <returns>AMF_WRAPPER_OK on success</returns>
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
/// <param name="handle">Encoder handle</param>
/// <param name="bitrateKbps">New target bitrate in kbps</param>
/// <returns>AMF_WRAPPER_OK on success</returns>
AMFWRAPPER_API int AmfSetBitrate(AmfEncoderHandle handle, int bitrateKbps);

/// <summary>
/// Create an AMF encoder instance with BGRA input support.
/// AMF internally converts BGRA to NV12 in hardware - no CPU/shader conversion needed.
/// </summary>
/// <param name="outHandle">Output encoder handle</param>
/// <param name="d3d11Device">D3D11 device to use for encoding</param>
/// <param name="width">Video width</param>
/// <param name="height">Video height</param>
/// <param name="fps">Frames per second</param>
/// <param name="bitrate">Target bitrate in kbps</param>
/// <returns>AMF_WRAPPER_OK on success</returns>
AMFWRAPPER_API int AmfCreateEncoderBgra(
    AmfEncoderHandle* outHandle,
    ID3D11Device* d3d11Device,
    int width,
    int height,
    int fps,
    int bitrate
);

/// <summary>
/// Encode a D3D11 BGRA texture directly (zero-copy, AMF converts internally)
/// Use with encoder created via AmfCreateEncoderBgra()
/// </summary>
/// <param name="handle">Encoder handle</param>
/// <param name="bgraTexture">D3D11 BGRA texture to encode</param>
/// <param name="forceKeyframe">Force IDR frame</param>
/// <returns>AMF_WRAPPER_OK on success</returns>
AMFWRAPPER_API int AmfEncodeBgraTexture(
    AmfEncoderHandle handle,
    ID3D11Texture2D* bgraTexture,
    int forceKeyframe
);

#ifdef __cplusplus
}
#endif
