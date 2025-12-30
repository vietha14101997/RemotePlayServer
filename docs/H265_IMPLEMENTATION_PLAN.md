# H.265/HEVC Implementation Plan for VR Remote Desktop

> **Document Version**: 1.0
> **Date**: 2025-12-29
> **Status**: Approved for Implementation

---

## Executive Summary

This document outlines the implementation plan for adding H.265/HEVC codec support to the VRWorkSpace remote desktop streaming system. The goal is to improve video quality while reducing bandwidth requirements over WiFi networks.

**Key Challenge**: Unity WebRTC 3.0.0-pre.8 does NOT support H.265/HEVC decoding natively.

**Solution**: Implement a Native Android Plugin using MediaCodec for hardware-accelerated HEVC decoding, with automatic fallback to H.264 via Unity WebRTC.

---

## Table of Contents

1. [Current System Architecture](#current-system-architecture)
2. [Problem Analysis](#problem-analysis)
3. [Proposed Architecture](#proposed-architecture)
4. [Implementation Phases](#implementation-phases)
5. [Server Changes](#phase-1-server-h265-encoding)
6. [Client Native Plugin](#phase-2-client-native-plugin)
7. [Integration](#phase-3-integration)
8. [Optimization](#phase-4-optimization)
9. [File References](#file-references)
10. [Testing Strategy](#testing-strategy)

---

## Current System Architecture

### Server (RemotePlayServer - Windows)
| Component | Technology |
|-----------|------------|
| Framework | .NET 9.0 |
| Screen Capture | DXGI Desktop Duplication |
| Color Conversion | GPU Compute Shader (BGRA→NV12) |
| Encoding | Hardware H.264 (NVENC/AMF/QSV) via LibAv |
| Streaming | WebRTC (SIPSorcery) |
| Protocol | 3-phase custom protocol over WebSocket |

### Client (VRWorkSpace - Android VR)
| Component | Technology |
|-----------|------------|
| Platform | Unity 2022.3.62f3 |
| VR Framework | Google Cardboard XR |
| Decoding | Unity WebRTC 3.0.0-pre.8 (H.264 only) |
| Display | Multi-panel VR with Trilinear + Aniso x8 |

---

## Problem Analysis

### Why H.265?

| Aspect | H.264 | H.265/HEVC |
|--------|-------|------------|
| Bitrate Efficiency | Baseline | 30-50% better |
| 4K Support | Limited | Excellent |
| Low Bitrate Quality | Artifacts visible | Cleaner image |
| Hardware Support | Universal | Modern devices (2015+) |

### Current Image Quality Issues

1. **Block artifacts** at low bitrate (WiFi congestion)
2. **Mosquito noise** around high-contrast edges
3. **Error propagation** due to long keyframe intervals
4. **Color banding** from aggressive compression

### Unity WebRTC Limitation

**Supported codecs in Unity WebRTC 3.0.0-pre.8:**
- H.264 (hardware accelerated)
- VP8, VP9 (software)
- AV1 (limited hardware support)

**NOT supported:** H.265/HEVC

**Solution:** Native Android plugin using MediaCodec API for direct hardware HEVC decoding.

---

## Proposed Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         SERVER (Windows)                             │
│                                                                      │
│  ┌──────────────┐    ┌────────────────┐    ┌──────────────────┐    │
│  │ DXGI Capture │───▶│ LibAvEncoder   │───▶│ WebRTC Streamer  │    │
│  │ (Desktop)    │    │ (H.265/H.264)  │    │ (SIPSorcery)     │    │
│  └──────────────┘    └────────────────┘    └────────┬─────────┘    │
│                              ▲                       │              │
│                              │                       │              │
│                    Codec negotiation          RTP/SRTP              │
│                    (prefer H.265)                    │              │
└──────────────────────────────────────────────────────┼──────────────┘
                                                       │
                                                  WiFi/LAN
                                                       │
┌──────────────────────────────────────────────────────┼──────────────┐
│                         CLIENT (Android VR)          │              │
│                                                       ▼              │
│  ┌──────────────────┐    ┌────────────────────────────────┐        │
│  │ Unity WebRTC     │    │    Native Decoder Plugin       │        │
│  │ (Signaling only) │    │    ┌─────────────────────┐     │        │
│  │                  │◀───│    │  JNI Bridge         │     │        │
│  │ OnTrack callback │    │    └──────────┬──────────┘     │        │
│  │ → Raw RTP data   │    │               │                │        │
│  └────────┬─────────┘    │    ┌──────────▼──────────┐     │        │
│           │              │    │  MediaCodec (HEVC)  │     │        │
│           │              │    │  Hardware Decoder   │     │        │
│           │              │    └──────────┬──────────┘     │        │
│           │              │               │                │        │
│           │              │    ┌──────────▼──────────┐     │        │
│           │              │    │  YUV → Texture2D    │     │        │
│           │              │    │  GPU Upload         │     │        │
│           │              │    └──────────┬──────────┘     │        │
│           │              └───────────────┼────────────────┘        │
│           │                              │                          │
│           │                              ▼                          │
│  ┌────────▼──────────────────────────────────────────┐             │
│  │              WorldPanelPlus (VR Display)          │             │
│  │              Texture2D → Material → Mesh          │             │
│  └───────────────────────────────────────────────────┘             │
└─────────────────────────────────────────────────────────────────────┘
```

### Codec Negotiation Flow

```
Phase 1: Capability Exchange
┌────────┐                           ┌────────┐
│ Client │                           │ Server │
└────┬───┘                           └────┬───┘
     │   client_capabilities             │
     │   { codecs: ["H265", "H264"] }    │
     │ ─────────────────────────────────▶│
     │                                    │
     │   server_codec_selection          │
     │   { selected: "H265" }            │
     │ ◀─────────────────────────────────│
     │                                    │

Phase 2: WebRTC SDP Exchange
     │   SDP Offer (H.265 preferred)     │
     │ ◀─────────────────────────────────│
     │                                    │
     │   SDP Answer (H.265 accepted)     │
     │ ─────────────────────────────────▶│
     │                                    │

Phase 3: Streaming
     │   RTP/SRTP (H.265 NAL units)     │
     │ ◀─────────────────────────────────│
     │                                    │
     │   Client decodes via MediaCodec   │
     │                                    │
```

---

## Implementation Phases

| Phase | Description | Effort | Impact |
|-------|-------------|--------|--------|
| **1** | Server H.265 Encoding | 2-3 days | High |
| **2** | Native Android HEVC Decoder | 3-5 days | Very High |
| **3** | Integration & Codec Negotiation | 2-3 days | High |
| **4** | Optimization & Tuning | 1-2 days | Medium |

**Total Estimated Effort**: 8-13 days

---

## Phase 1: Server H.265 Encoding

### 1.1 Modify LibAvEncoder.cs

**File**: `Encoding/LibAvEncoder.cs`
**Lines**: 175-218

Add VideoCodec enum and modify SelectEncoder():

```csharp
/// <summary>
/// Supported video codecs for encoding
/// </summary>
public enum VideoCodec
{
    H264,   // AVC - Universal compatibility
    H265    // HEVC - Better quality at lower bitrate
}

/// <summary>
/// Select hardware encoder based on GPU vendor and preferred codec
/// </summary>
private AVCodec* SelectEncoder(VideoCodec preferredCodec = VideoCodec.H265)
{
    AVCodec* codec = null;

    // Try H.265 first if preferred
    if (preferredCodec == VideoCodec.H265)
    {
        switch (_gpuVendor)
        {
            case GpuVendor.NVIDIA:
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_nvenc");
                if (codec != null)
                {
                    _encoderName = "hevc_nvenc";
                    _currentCodec = VideoCodec.H265;
                    return codec;
                }
                break;

            case GpuVendor.AMD:
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_amf");
                if (codec != null)
                {
                    _encoderName = "hevc_amf";
                    _currentCodec = VideoCodec.H265;
                    return codec;
                }
                break;

            case GpuVendor.Intel:
                codec = ffmpeg.avcodec_find_encoder_by_name("hevc_qsv");
                if (codec != null)
                {
                    _encoderName = "hevc_qsv";
                    _currentCodec = VideoCodec.H265;
                    return codec;
                }
                break;
        }

        Console.WriteLine($"[LibAv] H.265 encoder not available, falling back to H.264");
    }

    // Fallback to H.264 (existing logic)
    return SelectH264Encoder();
}
```

### 1.2 Add HEVC Encoder Configuration

**File**: `Encoding/LibAvEncoder.cs`

```csharp
/// <summary>
/// Configure HEVC-specific encoder options for optimal quality/latency
/// </summary>
private void ConfigureHevcOptions(AVCodecContext* ctx)
{
    switch (_encoderName)
    {
        case "hevc_nvenc":
            // NVIDIA NVENC HEVC settings
            ffmpeg.av_opt_set(ctx->priv_data, "preset", "p4", 0);      // balanced
            ffmpeg.av_opt_set(ctx->priv_data, "tune", "ll", 0);        // low latency
            ffmpeg.av_opt_set(ctx->priv_data, "rc", "vbr", 0);         // VBR mode
            ffmpeg.av_opt_set(ctx->priv_data, "cq", "25", 0);          // quality level
            ffmpeg.av_opt_set(ctx->priv_data, "spatial-aq", "1", 0);   // spatial AQ
            ffmpeg.av_opt_set(ctx->priv_data, "temporal-aq", "1", 0);  // temporal AQ
            ffmpeg.av_opt_set(ctx->priv_data, "rc-lookahead", "0", 0); // no lookahead
            break;

        case "hevc_amf":
            // AMD AMF HEVC settings
            ffmpeg.av_opt_set(ctx->priv_data, "quality", "balanced", 0);
            ffmpeg.av_opt_set(ctx->priv_data, "rc", "vbr_latency", 0);
            ffmpeg.av_opt_set(ctx->priv_data, "preanalysis", "true", 0);
            ffmpeg.av_opt_set(ctx->priv_data, "vbaq", "true", 0);
            break;

        case "hevc_qsv":
            // Intel QSV HEVC settings
            ffmpeg.av_opt_set(ctx->priv_data, "preset", "medium", 0);
            ffmpeg.av_opt_set(ctx->priv_data, "global_quality", "25", 0);
            break;
    }

    // Common HEVC settings
    ctx->gop_size = _fps;              // Keyframe every second
    ctx->max_b_frames = 0;             // No B-frames for lowest latency
    ctx->bit_rate = _bitrate;
    ctx->rc_max_rate = _bitrate * 2;   // Allow 2x peak
    ctx->rc_buffer_size = _bitrate;    // 1 second buffer

    // HEVC profile: Main Profile, Level 4.0 (supports 1080p60, 4K30)
    ctx->profile = FF_PROFILE_HEVC_MAIN;
    ctx->level = 120;  // Level 4.0
}
```

### 1.3 Modify EncoderFactory.cs

**File**: `Encoding/EncoderFactory.cs`
**Lines**: 28-131

```csharp
/// <summary>
/// Create WebRTC streamer with specified codec preference
/// </summary>
public static IWebRTCStreamer CreateStreamer(
    int fps,
    int kbps,
    EncoderMode mode = EncoderMode.LibAv,
    ID3D11Device? device = null,
    VideoCodec preferredCodec = VideoCodec.H265,  // NEW: Default to H.265
    int crf = 23,
    string preset = "p4",  // Changed: balanced quality
    bool zerolatency = true)
{
    var gpuVendor = GpuVendorDetector.DetectPrimaryGpuVendor();

    Console.WriteLine($"[EncoderFactory] Creating streamer: " +
        $"fps={fps}, kbps={kbps}, codec={preferredCodec}, gpu={gpuVendor}");

    // Pass codec preference to encoder
    return new WebRTCStreamerLibAvWrapper(
        fps, kbps, device, preferredCodec, crf, preset, zerolatency);
}
```

### 1.4 Modify WebRTC Codec Advertisement

**File**: `Encoding/WebRTCStreamer_LibAv.cs`
**Lines**: 158-178

```csharp
/// <summary>
/// Create media track with both H.265 and H.264 capabilities
/// </summary>
private MediaStreamTrack CreateVideoTrack()
{
    var capabilities = new List<SDPAudioVideoMediaFormat>();

    // Prefer H.265 if encoder supports it
    if (_encoder.CurrentCodec == VideoCodec.H265)
    {
        var h265 = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            id: 96,
            name: "H265",
            clockRate: 90000,
            channels: 0,
            fmtp: "profile-id=1;level-id=120");  // Main Profile, Level 4.0
        capabilities.Add(h265);
    }

    // Always include H.264 as fallback
    var h264 = new SDPAudioVideoMediaFormat(
        SDPMediaTypesEnum.video,
        id: 97,
        name: "H264",
        clockRate: 90000,
        channels: 0,
        fmtp: "packetization-mode=1;level-asymmetry-allowed=1;profile-level-id=42e01f");
    capabilities.Add(h264);

    var track = new MediaStreamTrack(
        SDPMediaTypesEnum.video,
        isRemote: false,
        capabilities: capabilities,
        streamStatus: MediaStreamStatusEnum.SendOnly);

    return track;
}
```

---

## Phase 2: Client Native Plugin

### 2.1 Plugin Directory Structure

```
VRWorkSpace/
├── Assets/
│   └── Plugins/
│       └── Android/
│           ├── HevcDecoder.aar           # Compiled AAR
│           └── libs/
│               └── arm64-v8a/
│                   └── libhevc_decoder.so
│
├── NativePlugins/
│   └── HevcDecoder/
│       ├── build.gradle
│       ├── CMakeLists.txt
│       ├── gradle.properties
│       └── src/
│           └── main/
│               ├── AndroidManifest.xml
│               ├── cpp/
│               │   ├── hevc_decoder.h
│               │   ├── hevc_decoder.cpp
│               │   ├── jni_bridge.cpp
│               │   └── rtp_depacketizer.cpp
│               └── java/
│                   └── com/
│                       └── vrworkspace/
│                           └── hevc/
│                               └── HevcDecoderBridge.java
```

### 2.2 Native C++ Implementation

**File**: `NativePlugins/HevcDecoder/src/main/cpp/hevc_decoder.h`

```cpp
#pragma once

#include <media/NdkMediaCodec.h>
#include <media/NdkMediaFormat.h>
#include <android/native_window.h>
#include <cstdint>
#include <vector>
#include <mutex>

namespace vrworkspace {

class HevcDecoder {
public:
    HevcDecoder();
    ~HevcDecoder();

    // Initialize decoder with resolution
    bool Initialize(int width, int height, bool lowLatency = true);

    // Decode NAL unit, returns true if frame is ready
    bool DecodeNal(const uint8_t* nalData, size_t nalSize, int64_t pts);

    // Get decoded frame (YUV420)
    bool GetDecodedFrame(uint8_t* yPlane, uint8_t* uvPlane, int* width, int* height);

    // Check if H.265 hardware decoder is available
    static bool IsHardwareDecoderAvailable();

    // Release resources
    void Release();

private:
    AMediaCodec* codec_ = nullptr;
    AMediaFormat* format_ = nullptr;

    int width_ = 0;
    int height_ = 0;
    int stride_ = 0;

    std::mutex mutex_;
    std::vector<uint8_t> frameBuffer_;
    bool frameReady_ = false;

    void ProcessOutputBuffer(ssize_t index, AMediaCodecBufferInfo* info);
};

} // namespace vrworkspace
```

**File**: `NativePlugins/HevcDecoder/src/main/cpp/hevc_decoder.cpp`

```cpp
#include "hevc_decoder.h"
#include <android/log.h>

#define LOG_TAG "HevcDecoder"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace vrworkspace {

HevcDecoder::HevcDecoder() = default;

HevcDecoder::~HevcDecoder() {
    Release();
}

bool HevcDecoder::IsHardwareDecoderAvailable() {
    AMediaCodec* testCodec = AMediaCodec_createDecoderByType("video/hevc");
    if (testCodec) {
        AMediaCodec_delete(testCodec);
        return true;
    }
    return false;
}

bool HevcDecoder::Initialize(int width, int height, bool lowLatency) {
    std::lock_guard<std::mutex> lock(mutex_);

    width_ = width;
    height_ = height;

    // Create HEVC decoder
    codec_ = AMediaCodec_createDecoderByType("video/hevc");
    if (!codec_) {
        LOGE("Failed to create HEVC decoder");
        return false;
    }

    // Configure format
    format_ = AMediaFormat_new();
    AMediaFormat_setString(format_, AMEDIAFORMAT_KEY_MIME, "video/hevc");
    AMediaFormat_setInt32(format_, AMEDIAFORMAT_KEY_WIDTH, width);
    AMediaFormat_setInt32(format_, AMEDIAFORMAT_KEY_HEIGHT, height);
    AMediaFormat_setInt32(format_, AMEDIAFORMAT_KEY_COLOR_FORMAT,
        COLOR_FormatYUV420Flexible);

    // Low latency settings
    if (lowLatency) {
        AMediaFormat_setInt32(format_, "low-latency", 1);
        AMediaFormat_setInt32(format_, "priority", 0);  // Realtime
    }

    // Configure decoder
    media_status_t status = AMediaCodec_configure(
        codec_, format_, nullptr, nullptr, 0);
    if (status != AMEDIA_OK) {
        LOGE("Failed to configure decoder: %d", status);
        return false;
    }

    // Start decoder
    status = AMediaCodec_start(codec_);
    if (status != AMEDIA_OK) {
        LOGE("Failed to start decoder: %d", status);
        return false;
    }

    // Allocate frame buffer (YUV420: width * height * 1.5)
    frameBuffer_.resize(width * height * 3 / 2);

    LOGI("HEVC decoder initialized: %dx%d", width, height);
    return true;
}

bool HevcDecoder::DecodeNal(const uint8_t* nalData, size_t nalSize, int64_t pts) {
    if (!codec_) return false;

    // Get input buffer
    ssize_t inputIdx = AMediaCodec_dequeueInputBuffer(codec_, 0);
    if (inputIdx < 0) {
        // No input buffer available, try again later
        return false;
    }

    size_t bufSize;
    uint8_t* buf = AMediaCodec_getInputBuffer(codec_, inputIdx, &bufSize);
    if (!buf || bufSize < nalSize) {
        LOGE("Input buffer too small: %zu < %zu", bufSize, nalSize);
        AMediaCodec_queueInputBuffer(codec_, inputIdx, 0, 0, pts, 0);
        return false;
    }

    // Copy NAL data
    memcpy(buf, nalData, nalSize);

    // Queue input buffer
    AMediaCodec_queueInputBuffer(codec_, inputIdx, 0, nalSize, pts, 0);

    // Check for output
    AMediaCodecBufferInfo info;
    ssize_t outputIdx = AMediaCodec_dequeueOutputBuffer(codec_, &info, 0);

    if (outputIdx >= 0) {
        ProcessOutputBuffer(outputIdx, &info);
        return true;
    }

    return false;
}

void HevcDecoder::ProcessOutputBuffer(ssize_t index, AMediaCodecBufferInfo* info) {
    std::lock_guard<std::mutex> lock(mutex_);

    size_t bufSize;
    uint8_t* buf = AMediaCodec_getOutputBuffer(codec_, index, &bufSize);

    if (buf && info->size > 0) {
        // Get actual format (may have changed)
        AMediaFormat* outputFormat = AMediaCodec_getOutputFormat(codec_);
        int32_t actualWidth, actualHeight, actualStride;
        AMediaFormat_getInt32(outputFormat, AMEDIAFORMAT_KEY_WIDTH, &actualWidth);
        AMediaFormat_getInt32(outputFormat, AMEDIAFORMAT_KEY_HEIGHT, &actualHeight);
        AMediaFormat_getInt32(outputFormat, AMEDIAFORMAT_KEY_STRIDE, &actualStride);
        AMediaFormat_delete(outputFormat);

        stride_ = actualStride > 0 ? actualStride : actualWidth;

        // Copy Y plane
        size_t ySize = stride_ * actualHeight;
        size_t uvSize = stride_ * actualHeight / 2;

        if (frameBuffer_.size() >= ySize + uvSize) {
            memcpy(frameBuffer_.data(), buf, ySize);
            memcpy(frameBuffer_.data() + ySize, buf + ySize, uvSize);
            frameReady_ = true;
        }
    }

    AMediaCodec_releaseOutputBuffer(codec_, index, false);
}

bool HevcDecoder::GetDecodedFrame(uint8_t* yPlane, uint8_t* uvPlane,
                                   int* outWidth, int* outHeight) {
    std::lock_guard<std::mutex> lock(mutex_);

    if (!frameReady_) return false;

    size_t ySize = stride_ * height_;
    size_t uvSize = stride_ * height_ / 2;

    memcpy(yPlane, frameBuffer_.data(), ySize);
    memcpy(uvPlane, frameBuffer_.data() + ySize, uvSize);

    *outWidth = width_;
    *outHeight = height_;

    frameReady_ = false;
    return true;
}

void HevcDecoder::Release() {
    std::lock_guard<std::mutex> lock(mutex_);

    if (codec_) {
        AMediaCodec_stop(codec_);
        AMediaCodec_delete(codec_);
        codec_ = nullptr;
    }

    if (format_) {
        AMediaFormat_delete(format_);
        format_ = nullptr;
    }

    frameBuffer_.clear();
    frameReady_ = false;

    LOGI("HEVC decoder released");
}

} // namespace vrworkspace
```

### 2.3 JNI Bridge

**File**: `NativePlugins/HevcDecoder/src/main/cpp/jni_bridge.cpp`

```cpp
#include <jni.h>
#include "hevc_decoder.h"

using namespace vrworkspace;

extern "C" {

JNIEXPORT jboolean JNICALL
Java_com_vrworkspace_hevc_HevcDecoderBridge_nativeIsAvailable(
    JNIEnv* env, jclass clazz) {
    return HevcDecoder::IsHardwareDecoderAvailable() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jlong JNICALL
Java_com_vrworkspace_hevc_HevcDecoderBridge_nativeCreate(
    JNIEnv* env, jobject thiz, jint width, jint height, jboolean lowLatency) {

    HevcDecoder* decoder = new HevcDecoder();
    if (decoder->Initialize(width, height, lowLatency)) {
        return reinterpret_cast<jlong>(decoder);
    }

    delete decoder;
    return 0;
}

JNIEXPORT jboolean JNICALL
Java_com_vrworkspace_hevc_HevcDecoderBridge_nativeDecode(
    JNIEnv* env, jobject thiz, jlong handle,
    jbyteArray nalData, jlong pts) {

    if (handle == 0) return JNI_FALSE;

    HevcDecoder* decoder = reinterpret_cast<HevcDecoder*>(handle);

    jsize nalSize = env->GetArrayLength(nalData);
    jbyte* nalBytes = env->GetByteArrayElements(nalData, nullptr);

    bool result = decoder->DecodeNal(
        reinterpret_cast<const uint8_t*>(nalBytes), nalSize, pts);

    env->ReleaseByteArrayElements(nalData, nalBytes, JNI_ABORT);

    return result ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_com_vrworkspace_hevc_HevcDecoderBridge_nativeGetFrame(
    JNIEnv* env, jobject thiz, jlong handle,
    jbyteArray yPlane, jbyteArray uvPlane,
    jintArray outSize) {

    if (handle == 0) return JNI_FALSE;

    HevcDecoder* decoder = reinterpret_cast<HevcDecoder*>(handle);

    jbyte* yBytes = env->GetByteArrayElements(yPlane, nullptr);
    jbyte* uvBytes = env->GetByteArrayElements(uvPlane, nullptr);
    jint* sizeBytes = env->GetIntArrayElements(outSize, nullptr);

    int width, height;
    bool result = decoder->GetDecodedFrame(
        reinterpret_cast<uint8_t*>(yBytes),
        reinterpret_cast<uint8_t*>(uvBytes),
        &width, &height);

    if (result) {
        sizeBytes[0] = width;
        sizeBytes[1] = height;
    }

    env->ReleaseByteArrayElements(yPlane, yBytes, 0);
    env->ReleaseByteArrayElements(uvPlane, uvBytes, 0);
    env->ReleaseIntArrayElements(outSize, sizeBytes, 0);

    return result ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_com_vrworkspace_hevc_HevcDecoderBridge_nativeRelease(
    JNIEnv* env, jobject thiz, jlong handle) {

    if (handle == 0) return;

    HevcDecoder* decoder = reinterpret_cast<HevcDecoder*>(handle);
    decoder->Release();
    delete decoder;
}

} // extern "C"
```

### 2.4 Java Bridge Class

**File**: `NativePlugins/HevcDecoder/src/main/java/com/vrworkspace/hevc/HevcDecoderBridge.java`

```java
package com.vrworkspace.hevc;

public class HevcDecoderBridge {

    static {
        System.loadLibrary("hevc_decoder");
    }

    private long nativeHandle = 0;
    private int width;
    private int height;
    private byte[] yBuffer;
    private byte[] uvBuffer;
    private int[] sizeBuffer = new int[2];

    /**
     * Check if hardware HEVC decoder is available
     */
    public static native boolean nativeIsAvailable();

    /**
     * Check availability (static method for Unity)
     */
    public static boolean isAvailable() {
        try {
            return nativeIsAvailable();
        } catch (Exception e) {
            return false;
        }
    }

    /**
     * Initialize decoder with resolution
     */
    public boolean initialize(int width, int height) {
        return initialize(width, height, true);
    }

    public boolean initialize(int width, int height, boolean lowLatency) {
        this.width = width;
        this.height = height;

        // Allocate buffers
        int ySize = width * height;
        int uvSize = width * height / 2;
        yBuffer = new byte[ySize];
        uvBuffer = new byte[uvSize];

        nativeHandle = nativeCreate(width, height, lowLatency);
        return nativeHandle != 0;
    }

    /**
     * Decode NAL unit
     */
    public boolean decode(byte[] nalData, long pts) {
        if (nativeHandle == 0) return false;
        return nativeDecode(nativeHandle, nalData, pts);
    }

    /**
     * Decode NAL unit with auto PTS
     */
    public boolean decode(byte[] nalData) {
        return decode(nalData, System.nanoTime() / 1000);
    }

    /**
     * Get decoded frame Y plane
     */
    public byte[] getYPlane() {
        if (nativeHandle == 0) return null;
        if (nativeGetFrame(nativeHandle, yBuffer, uvBuffer, sizeBuffer)) {
            return yBuffer;
        }
        return null;
    }

    /**
     * Get decoded frame UV plane
     */
    public byte[] getUVPlane() {
        return uvBuffer;
    }

    /**
     * Get frame dimensions
     */
    public int getWidth() { return sizeBuffer[0]; }
    public int getHeight() { return sizeBuffer[1]; }

    /**
     * Release resources
     */
    public void release() {
        if (nativeHandle != 0) {
            nativeRelease(nativeHandle);
            nativeHandle = 0;
        }
        yBuffer = null;
        uvBuffer = null;
    }

    // Native methods
    private native long nativeCreate(int width, int height, boolean lowLatency);
    private native boolean nativeDecode(long handle, byte[] nalData, long pts);
    private native boolean nativeGetFrame(long handle, byte[] yPlane, byte[] uvPlane, int[] outSize);
    private native void nativeRelease(long handle);
}
```

### 2.5 Unity C# Wrapper

**File**: `Assets/VR-Workspace/Scripts/Native/HevcDecoderPlugin.cs`

```csharp
using System;
using UnityEngine;

namespace VRWorkspace.Native
{
    /// <summary>
    /// Unity wrapper for native Android HEVC decoder
    /// </summary>
    public class HevcDecoderPlugin : IDisposable
    {
        private AndroidJavaObject _bridge;
        private Texture2D _outputTexture;
        private byte[] _yuvBuffer;
        private int _width;
        private int _height;
        private bool _disposed;

        public Texture2D OutputTexture => _outputTexture;
        public int Width => _width;
        public int Height => _height;
        public bool IsInitialized => _bridge != null;

        /// <summary>
        /// Check if hardware HEVC decoder is available
        /// </summary>
        public static bool IsAvailable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var bridgeClass = new AndroidJavaClass("com.vrworkspace.hevc.HevcDecoderBridge");
                return bridgeClass.CallStatic<bool>("isAvailable");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HevcDecoder] Availability check failed: {e.Message}");
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Initialize decoder with resolution
        /// </summary>
        public bool Initialize(int width, int height, bool lowLatency = true)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                _width = width;
                _height = height;

                _bridge = new AndroidJavaObject("com.vrworkspace.hevc.HevcDecoderBridge");
                bool success = _bridge.Call<bool>("initialize", width, height, lowLatency);

                if (success)
                {
                    // Create output texture (YUV format, will need shader conversion)
                    _outputTexture = new Texture2D(width, height, TextureFormat.R8, false);
                    _outputTexture.filterMode = FilterMode.Bilinear;
                    _outputTexture.wrapMode = TextureWrapMode.Clamp;

                    // YUV420: Y plane = width*height, UV plane = width*height/2
                    _yuvBuffer = new byte[width * height * 3 / 2];

                    Debug.Log($"[HevcDecoder] Initialized: {width}x{height}");
                    return true;
                }

                Debug.LogError("[HevcDecoder] Native initialization failed");
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HevcDecoder] Init exception: {e}");
                return false;
            }
#else
            Debug.LogWarning("[HevcDecoder] Only supported on Android");
            return false;
#endif
        }

        /// <summary>
        /// Decode NAL unit
        /// </summary>
        public bool DecodeNal(byte[] nalData)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return false;

            try
            {
                return _bridge.Call<bool>("decode", nalData);
            }
            catch (Exception e)
            {
                Debug.LogError($"[HevcDecoder] Decode error: {e.Message}");
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Get decoded frame and update texture
        /// </summary>
        public bool UpdateTexture()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null || _outputTexture == null) return false;

            try
            {
                byte[] yPlane = _bridge.Call<byte[]>("getYPlane");
                if (yPlane == null) return false;

                // Upload Y plane to texture (UV needs shader conversion)
                _outputTexture.LoadRawTextureData(yPlane);
                _outputTexture.Apply(false);

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HevcDecoder] UpdateTexture error: {e.Message}");
                return false;
            }
#else
            return false;
#endif
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                _bridge?.Call("release");
                _bridge?.Dispose();
            }
            catch { }
#endif

            if (_outputTexture != null)
            {
                UnityEngine.Object.Destroy(_outputTexture);
                _outputTexture = null;
            }

            _yuvBuffer = null;
            _bridge = null;

            Debug.Log("[HevcDecoder] Disposed");
        }
    }
}
```

---

## Phase 3: Integration

### 3.1 Protocol Changes

Add codec capability exchange to Phase 1 of the V2 protocol.

**Server-side** (`Program.cs`):

```csharp
// Handle client capabilities message
case "client_capabilities":
{
    var codecs = msg.GetProperty("codecs").EnumerateArray()
        .Select(c => c.GetString()).ToList();
    var preferH265 = msg.GetProperty("preferH265").GetBoolean();

    // Determine codec to use
    string selectedCodec = "H264";  // Default fallback

    if (preferH265 && codecs.Contains("H265"))
    {
        // Check if server can encode H.265
        if (CanEncodeH265())
        {
            selectedCodec = "H265";
        }
    }

    // Send codec selection
    await SendAsync(new {
        type = "codec_selection",
        codec = selectedCodec
    });

    _selectedCodec = selectedCodec;
    break;
}
```

**Client-side** (`PhaseProtocolClient.cs`):

```csharp
private async Task SendClientCapabilities()
{
    var codecs = new List<string>();

    // Check native H.265 decoder availability
#if UNITY_ANDROID && !UNITY_EDITOR
    if (HevcDecoderPlugin.IsAvailable())
    {
        codecs.Add("H265");
    }
#endif

    // H.264 always available via Unity WebRTC
    codecs.Add("H264");

    var msg = new {
        type = "client_capabilities",
        codecs = codecs,
        preferH265 = codecs.Contains("H265")
    };

    await SendJsonAsync(msg);
}
```

### 3.2 Decoder Selection in Client

**File**: `MultiPCStreamClient.cs`

```csharp
public enum DecoderMode
{
    UnityWebRTC,    // H.264 via Unity WebRTC
    NativeHevc      // H.265 via native plugin
}

private DecoderMode _decoderMode = DecoderMode.UnityWebRTC;
private HevcDecoderPlugin _hevcDecoder;

private void SetupDecoder(string codec, int width, int height)
{
    if (codec == "H265")
    {
        _decoderMode = DecoderMode.NativeHevc;
        _hevcDecoder = new HevcDecoderPlugin();

        if (!_hevcDecoder.Initialize(width, height))
        {
            Debug.LogError("[MultiPC] Failed to init HEVC decoder, falling back to H.264");
            _decoderMode = DecoderMode.UnityWebRTC;
            _hevcDecoder?.Dispose();
            _hevcDecoder = null;
        }
        else
        {
            Debug.Log("[MultiPC] Using Native HEVC decoder");
        }
    }
    else
    {
        _decoderMode = DecoderMode.UnityWebRTC;
        Debug.Log("[MultiPC] Using Unity WebRTC H.264 decoder");
    }
}
```

---

## Phase 4: Optimization

### 4.1 Encoder Bitrate Recommendations

| Network | Resolution | H.264 Bitrate | H.265 Bitrate |
|---------|------------|---------------|---------------|
| LAN Ethernet | 1080p60 | 20-30 Mbps | 12-18 Mbps |
| WiFi 5GHz | 1080p60 | 15-20 Mbps | 8-12 Mbps |
| WiFi 5GHz | 1080p30 | 8-12 Mbps | 5-8 Mbps |
| WiFi 2.4GHz | 720p30 | 4-6 Mbps | 3-4 Mbps |

### 4.2 YUV→RGB Shader

For efficient YUV to RGB conversion on GPU:

**File**: `Assets/Shaders/YUV2RGB.shader`

```hlsl
Shader "VRWorkspace/YUV2RGB"
{
    Properties
    {
        _YTex ("Y Texture", 2D) = "white" {}
        _UVTex ("UV Texture", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _YTex;
            sampler2D _UVTex;

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 pos : SV_POSITION;
            };

            v2f vert(float4 pos : POSITION, float2 uv : TEXCOORD0)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(pos);
                o.uv = uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float y = tex2D(_YTex, i.uv).r;
                float2 uv = tex2D(_UVTex, i.uv).rg;

                // BT.709 YUV to RGB conversion
                float u = uv.r - 0.5;
                float v = uv.g - 0.5;

                float r = y + 1.5748 * v;
                float g = y - 0.1873 * u - 0.4681 * v;
                float b = y + 1.8556 * u;

                return fixed4(r, g, b, 1.0);
            }
            ENDCG
        }
    }
}
```

---

## File References

### Server Files to Modify

| File | Changes |
|------|---------|
| `Encoding/LibAvEncoder.cs` | Add H.265 encoder selection and configuration |
| `Encoding/EncoderFactory.cs` | Add VideoCodec parameter |
| `Encoding/WebRTCStreamer_LibAv.cs` | Multi-codec SDP advertisement |
| `Program.cs` | Codec negotiation in V2 protocol |

### Client Files to Modify

| File | Changes |
|------|---------|
| `Scripts/Streaming/PhaseProtocolClient.cs` | Codec capability exchange |
| `Scripts/Streaming/MultiPCStreamClient.cs` | Dual decoder mode support |

### New Files to Create

| File | Description |
|------|-------------|
| `Scripts/Native/HevcDecoderPlugin.cs` | Unity wrapper for native decoder |
| `Scripts/Native/RtpDepacketizer.cs` | H.265 RTP depacketization |
| `Shaders/YUV2RGB.shader` | YUV to RGB conversion shader |
| `NativePlugins/HevcDecoder/*` | Complete native Android project |

---

## Testing Strategy

### Unit Tests

1. **Encoder Selection Test**
   - Verify H.265 encoder is selected on NVIDIA/AMD/Intel
   - Verify fallback to H.264 when HEVC unavailable

2. **Native Decoder Test**
   - Test MediaCodec initialization
   - Test NAL unit decoding
   - Test frame extraction

### Integration Tests

1. **Codec Negotiation**
   - Client advertises H.265 support → Server selects H.265
   - Client doesn't support H.265 → Server falls back to H.264

2. **End-to-End Streaming**
   - H.265 stream plays correctly on Android
   - Automatic fallback works when codec mismatch

### Performance Benchmarks

| Metric | Target |
|--------|--------|
| Encode latency (server) | < 5ms |
| Decode latency (client) | < 5ms |
| Frame delivery (WiFi 5GHz) | < 30ms total |
| Bitrate efficiency | 30-50% reduction vs H.264 |

---

## References

- [Android MediaCodec API](https://developer.android.com/reference/android/media/MediaCodec)
- [FFmpeg HEVC Encoding](https://trac.ffmpeg.org/wiki/Encode/H.265)
- [SIPSorcery WebRTC](https://github.com/sipsorcery-org/sipsorcery)
- [Unity WebRTC Package](https://docs.unity3d.com/Packages/com.unity.webrtc@3.0/manual/index.html)
- [Parsec Low Latency Streaming](https://parsec.app/technology)
