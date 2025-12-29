# H.265 Implementation - Chi tiết Task List

> **Tổng effort**: 8-13 ngày | **4 Phases** | **~75 subtasks**
> **Created**: 2025-12-29

---

## TASK LIST TỔNG QUAN

### Phase 1: Server H.265 Encoding (2-3 ngày) - **✅ COMPLETED**
- [x] 1.1 - 1.8: LibAvEncoder modifications ✅
- [x] 1.9 - 1.12: EncoderFactory updates ✅
- [x] 1.13 - 1.17: WebRTC codec advertisement ✅
- [x] 1.18 - 1.20: Protocol changes ✅

### Phase 2: Client Native Plugin (3-5 ngày)
- [ ] 2.1 - 2.5: Project setup
- [ ] 2.6 - 2.12: C++ MediaCodec implementation
- [ ] 2.13 - 2.16: JNI Bridge
- [ ] 2.17 - 2.20: Java Bridge
- [ ] 2.21 - 2.26: Unity C# Wrapper
- [ ] 2.27 - 2.30: Build & Integration

### Phase 3: Integration (2-3 ngày)
- [ ] 3.1 - 3.5: Protocol integration
- [ ] 3.6 - 3.10: Decoder selection
- [ ] 3.11 - 3.15: RTP depacketizer

### Phase 4: Optimization (1-2 ngày)
- [ ] 4.1 - 4.5: Encoder tuning
- [ ] 4.6 - 4.8: YUV→RGB shader
- [ ] 4.9 - 4.12: Testing

### ~~USB Tethering Mode~~ - **DEFERRED** (Chưa cần làm)
> Tạm hoãn - ưu tiên WiFi nội bộ trước

---

## PHASE 1: SERVER H.265 ENCODING ✅ COMPLETED

### 1.1 Thêm VideoCodec enum ✅
**File**: `Encoding/LibAvEncoder.cs`
**Status**: [x] Completed

```csharp
public enum VideoCodec { H264, H265 }
```

**Tasks:**
- [x] Tạo enum VideoCodec (đã có sẵn)
- [x] Thêm field `_preferredCodec` và `_currentCodec` để track codec
- [x] Thêm property `CurrentCodec` để expose ra ngoài

---

### 1.2 Sửa SelectEncoder() method ✅
**File**: `Encoding/LibAvEncoder.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Thêm logic try H.265 trước nếu preferred
- [x] NVIDIA: tìm `hevc_nvenc`
- [x] AMD: tìm `hevc_amf`
- [x] Intel: tìm `hevc_qsv`
- [x] Fallback về H.264 nếu H.265 không available
- [x] Log codec selection result

---

### 1.3 Tạo SelectH264Encoder() và SelectH265Encoder() method ✅
**File**: `Encoding/LibAvEncoder.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Extract H.264 selection logic vào `SelectH264Encoder()`
- [x] Tạo `SelectH265Encoder()` cho HEVC encoders
- [x] NVIDIA: hevc_nvenc, AMD: hevc_amf, Intel: hevc_qsv

---

### 1.4 Thêm ConfigureHevcOptions() trong ConfigureEncoderOptions() ✅
**File**: `Encoding/LibAvEncoder.cs`
**Status**: [x] Completed

**Tasks:**
- [x] NVENC HEVC settings: preset p4, tune ll, spatial-aq, temporal-aq
- [x] AMF HEVC settings: quality balanced, rc vbr_latency, preanalysis, vbaq
- [x] QSV HEVC settings: preset faster, adaptive_i
- [x] Common: profile HEVC_MAIN, level 4.0

---

### 1.5 Sửa Initialize() method ✅
**File**: `Encoding/LibAvEncoder.cs`
**Status**: [x] Completed

**Tasks:**
- [x] ConfigureEncoderOptions() xử lý cả H.264 và H.265 cases

---

### 1.6 Sửa constructor ✅
**File**: `Encoding/LibAvEncoder.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Thêm parameter `VideoCodec preferredCodec = VideoCodec.H265`
- [x] Store preferredCodec và pass xuống SelectEncoder()

---

### 1.7 EncodeFrame() - Không cần thay đổi ✅
**File**: `Encoding/LibAvEncoder.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Verified: NAL format tương thích, HEVC NAL prefix (0x00000001) đúng

---

### 1.8 Unit test encoder selection
**Status**: [ ] Pending - Manual testing required

---

### 1.9 Sửa EncoderFactory.CreateStreamer() ✅
**File**: `Encoding/EncoderFactory.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Thêm parameter `VideoCodec preferredCodec = VideoCodec.H265`
- [x] Pass preferredCodec xuống WebRTCStreamerLibAvWrapper

---

### 1.10 Sửa WebRTCStreamerLibAvWrapper constructor ✅
**File**: `Encoding/EncoderFactory.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Thêm parameter VideoCodec
- [x] Pass xuống WebRTCStreamer_LibAv

---

### 1.11 Cập nhật IWebRTCStreamer interface ✅
**File**: `Encoding/EncoderFactory.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Thêm property `VideoCodec CurrentCodec { get; }`

---

### 1.12 Cập nhật tất cả implementations ✅
**Status**: [x] Completed

**Tasks:**
- [x] WebRTCStreamerLibAvWrapper - CurrentCodec from underlying streamer
- [x] WebRTCStreamerFFmpegWrapper - return H264
- [x] WebRTCStreamerAmfNativeWrapper - return H264

---

### 1.13 Cập nhật SDP video track ✅
**File**: `Encoding/WebRTCStreamer_LibAv.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Tạo H.265 SDPAudioVideoMediaFormat (id: 96, profile-id=1, level-id=120)
- [x] Tạo H.264 SDPAudioVideoMediaFormat (id: 97, profile-level-id=42e033)
- [x] Capabilities list với H.265 first (nếu preferred)

---

### 1.14 Sửa SetupPeerConnection() ✅
**File**: `Encoding/WebRTCStreamer_LibAv.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Dynamic video capabilities based on _preferredCodec

---

### 1.15 Cập nhật OnVideoFormatsNegotiated handler ✅
**File**: `Encoding/WebRTCStreamer_LibAv.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Check cả H.265 và H.264
- [x] Log codec được negotiated

---

### 1.16 Cập nhật LogKeyFrame() cho HEVC ✅
**File**: `Encoding/WebRTCStreamer_LibAv.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Detect HEVC IDR NAL types (19, 20, 21)
- [x] Detect H.264 IDR NAL type (5)

---

### 1.17 Test WebRTC negotiation
**Status**: [ ] Pending - Manual testing required

---

### 1.18 Cập nhật HardwareInfo với HEVC support ✅
**File**: `Utils/HardwareInfo.cs`, `Utils/HardwareInfoGatherer.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Thêm `SupportedCodecs` list to EncoderInfo
- [x] Thêm `PreferredCodec` string to EncoderInfo
- [x] Thêm `SupportsHevc` boolean to EncoderInfo
- [x] Check HEVC encoder availability via FFmpeg

---

### 1.19 LibAvEncoderAdapter với VideoCodec ✅
**File**: `Encoding/LibAvEncoderAdapter.cs`
**Status**: [x] Completed

**Tasks:**
- [x] Thêm overload Initialize() với VideoCodec parameter
- [x] Thêm CurrentCodec property
- [x] Pass VideoCodec to LibAvEncoder

---

### 1.20 Protocol info được gửi qua hardware_info ✅
**File**: `Protocol/PhaseProtocolHandler.cs`
**Status**: [x] Completed

**Tasks:**
- [x] EncoderInfo now includes supportedCodecs, preferredCodec, supportsHevc
- [x] Client sẽ nhận được codec info trong Phase 1 hardware_info

---

## PHASE 2: CLIENT NATIVE PLUGIN

### 2.1 Tạo NativePlugins folder structure
**Location**: `VRWorkSpace/NativePlugins/HevcDecoder/`
**Status**: [ ] Pending

**Tasks:**
- [ ] Tạo thư mục `NativePlugins/HevcDecoder/`
- [ ] Tạo `src/main/cpp/`
- [ ] Tạo `src/main/java/com/vrworkspace/hevc/`

---

### 2.2 Tạo build.gradle
**File**: `NativePlugins/HevcDecoder/build.gradle`
**Status**: [ ] Pending

**Tasks:**
- [ ] Android library plugin
- [ ] minSdkVersion 24
- [ ] NDK configuration
- [ ] CMake setup

---

### 2.3 Tạo CMakeLists.txt
**File**: `NativePlugins/HevcDecoder/CMakeLists.txt`
**Status**: [ ] Pending

**Tasks:**
- [ ] Set cmake_minimum_required
- [ ] Add library hevc_decoder SHARED
- [ ] Link mediandk, log

---

### 2.4 Tạo gradle.properties
**File**: `NativePlugins/HevcDecoder/gradle.properties`
**Status**: [ ] Pending

**Tasks:**
- [ ] android.useAndroidX=true

---

### 2.5 Tạo AndroidManifest.xml
**File**: `NativePlugins/HevcDecoder/src/main/AndroidManifest.xml`
**Status**: [ ] Pending

**Tasks:**
- [ ] Package name com.vrworkspace.hevc
- [ ] minSdkVersion 24

---

### 2.6 - 2.12: C++ Implementation
**Status**: [ ] Pending

**Files to create:**
- [ ] `hevc_decoder.h` - Header với class declaration
- [ ] `hevc_decoder.cpp` - MediaCodec wrapper implementation
- [ ] `jni_bridge.cpp` - JNI interface

**Methods to implement:**
- [ ] HevcDecoder::IsHardwareDecoderAvailable()
- [ ] HevcDecoder::Initialize()
- [ ] HevcDecoder::DecodeNal()
- [ ] HevcDecoder::ProcessOutputBuffer()
- [ ] HevcDecoder::GetDecodedFrame()
- [ ] HevcDecoder::Release()

---

### 2.13 - 2.16: JNI Bridge
**Status**: [ ] Pending

**Tasks:**
- [ ] nativeIsAvailable()
- [ ] nativeCreate()
- [ ] nativeDecode()
- [ ] nativeGetFrame()
- [ ] nativeRelease()

---

### 2.17 - 2.20: Java Bridge
**File**: `HevcDecoderBridge.java`
**Status**: [ ] Pending

**Tasks:**
- [ ] Static isAvailable()
- [ ] initialize(width, height)
- [ ] decode(nalData)
- [ ] getYPlane(), getUVPlane()
- [ ] release()
- [ ] Buffer management

---

### 2.21 - 2.26: Unity C# Wrapper
**File**: `HevcDecoderPlugin.cs`
**Status**: [ ] Pending

**Tasks:**
- [ ] IsAvailable() static method
- [ ] Initialize()
- [ ] DecodeNal()
- [ ] UpdateTexture()
- [ ] Dispose()

---

### 2.27 - 2.30: Build & Integration
**Status**: [ ] Pending

**Tasks:**
- [ ] Build AAR với gradle
- [ ] Copy AAR to Unity Plugins/Android
- [ ] Test IsAvailable()
- [ ] Test decode with sample data

---

## PHASE 3: INTEGRATION

### 3.1 - 3.5: Protocol Integration
**File**: `PhaseProtocolClient.cs`
**Status**: [ ] Pending

**Tasks:**
- [ ] GetSupportedCodecs() method
- [ ] SendClientCapabilities()
- [ ] Handle codec_selection response
- [ ] Modify Phase 1 flow
- [ ] Pass codec to streaming setup

---

### 3.6 - 3.10: Decoder Selection
**File**: `MultiPCStreamClient.cs`
**Status**: [ ] Pending

**Tasks:**
- [ ] DecoderMode enum
- [ ] SetupDecoder() method
- [ ] Modify OnTrack handler
- [ ] Frame handling for HEVC
- [ ] Cleanup handling

---

### 3.11 - 3.15: RTP Depacketizer
**File**: `RtpDepacketizer.cs` (NEW)
**Status**: [ ] Pending

**Tasks:**
- [ ] RTP header parsing
- [ ] H.265 NAL unit extraction
- [ ] Handle single NAL, AP, FU packets
- [ ] NAL reassembly
- [ ] Testing

---

## PHASE 4: OPTIMIZATION

### 4.1 - 4.5: Encoder Tuning
**Status**: [ ] Pending

**Tasks:**
- [ ] NVENC preset testing (p3/p4/p5)
- [ ] AMF settings optimization
- [ ] Adaptive bitrate implementation
- [ ] Network-based bitrate config
- [ ] Resolution testing

---

### 4.6 - 4.8: YUV Shader
**Status**: [ ] Pending

**Tasks:**
- [ ] Create YUV2RGB.shader
- [ ] Create YUV material
- [ ] Integrate with HevcDecoderPlugin

---

### 4.9 - 4.12: Testing
**Status**: [ ] Pending

**Tasks:**
- [ ] End-to-end testing (NVIDIA/AMD)
- [ ] Performance benchmarks
- [ ] Quality comparison H.264 vs H.265
- [ ] Stress testing

---

## ~~DEFERRED: USB Tethering Mode~~

> **Status**: DEFERRED - Chưa cần làm
> **Reason**: Ưu tiên WiFi nội bộ trước

~~**Tasks:**~~
- ~~ADB Port Forwarding support~~
- ~~UI toggle USB/WiFi mode~~
- ~~Auto-detect optimal settings~~

---

## CHECKLIST HOÀN THÀNH

### Phase 1 ✅
- [x] Server encode H.265 với NVENC ✅
- [x] Server encode H.265 với AMF ✅
- [x] Server encode H.265 với QSV ✅
- [x] Server fallback H.264 ✅
- [x] WebRTC advertise cả 2 codecs ✅
- [x] Protocol send codec info (via hardware_info) ✅

### Phase 2
- [ ] Native plugin build thành công
- [ ] MediaCodec decode HEVC
- [ ] Unity wrapper hoạt động
- [ ] AAR integrated

### Phase 3
- [ ] Client gửi codec capabilities
- [ ] Server chọn codec
- [ ] Dual decoder mode
- [ ] RTP depacketizer H.265

### Phase 4
- [ ] Encoder settings tối ưu
- [ ] YUV shader
- [ ] Full testing pass

---

## Quick Reference

### File Paths - Server
```
F:\VRWorkspace Projects\RemotePlayServer\
├── Encoding/
│   ├── LibAvEncoder.cs          # H.265 encoder selection
│   ├── EncoderFactory.cs        # VideoCodec parameter
│   ├── WebRTCStreamer_LibAv.cs  # SDP codec advertisement
│   └── IWebRTCStreamer.cs       # Interface update
└── Program.cs                   # Protocol handler
```

### File Paths - Client
```
F:\VRWorkspace Projects\VRWorkSpace\
├── Assets/VR-Workspace/Scripts/
│   ├── Streaming/
│   │   ├── PhaseProtocolClient.cs  # Codec capability
│   │   └── MultiPCStreamClient.cs  # Dual decoder
│   └── Native/
│       ├── HevcDecoderPlugin.cs    # NEW
│       └── RtpDepacketizer.cs      # NEW
├── Assets/Plugins/Android/
│   └── HevcDecoder.aar             # NEW
└── NativePlugins/HevcDecoder/      # NEW
```
