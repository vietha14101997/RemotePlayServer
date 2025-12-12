# Phân Tích Độ Trễ & Giải Pháp Remote Play Server

## 1. Phân Tích Kiến Trúc Hiện Tại

### 1.1 Pipeline Hiện Tại

```
┌─────────────┐    ┌──────────────┐    ┌─────────────┐    ┌─────────────┐    ┌────────┐
│ DXGI/WGC    │───▶│  GPU→CPU     │───▶│   FFmpeg    │───▶│    RTP      │───▶│ WebRTC │
│ Capture     │    │  Memory Copy │    │   Pipe      │    │ Packetizer  │    │ Stream │
└─────────────┘    └──────────────┘    └─────────────┘    └─────────────┘    └────────┘
     ~2ms              ~8-15ms            ~15-50ms            ~2ms             ~5ms
```

**Tổng độ trễ ước tính: 32-74ms** (chưa tính network latency)

### 1.2 Các Điểm Nghẽn Chính

| Điểm Nghẽn | Mô Tả | Độ Trễ |
|------------|-------|--------|
| **GPU→CPU Copy** | `Map()` staging texture, copy từng row | 8-15ms/frame |
| **FFmpeg Pipe Overhead** | Spawn process, stdin/stdout I/O | 5-10ms |
| **CPU Encoding Fallback** | libx264 khi không có GPU encoder | 30-100ms/frame |
| **Pixel Format Conversion** | BGRA → NV12/YUV420P | 5-10ms |
| **Frame Pacing** | Channel queuing và drop logic | 0-16ms |

### 1.3 Vấn Đề Với Phần Cứng Yếu

1. **Không có GPU Encoder (NVENC/AMF/QSV)**
   - Fallback libx264 → CPU 100%, 10-20 FPS max
   - Latency tăng gấp 3-5x

2. **iGPU Yếu (Intel UHD 620, etc.)**
   - QSV quality/speed trade-off kém
   - Shared memory bandwidth với CPU

3. **Laptop Không Có dGPU**
   - Capture + Encode cạnh tranh tài nguyên
   - Thermal throttling

---

## 2. Giải Pháp Trong Nền Tảng .NET Hiện Tại

### 2.1 Zero-Copy GPU Pipeline

**Concept**: Giữ texture trên GPU từ capture → encode, không copy về CPU.

```csharp
// Thay vì:
DxgiCapture → Map() → byte[] → FFmpeg stdin → Encode

// Chuyển thành:
DxgiCapture → ID3D11Texture2D → Media Foundation Hardware Encoder → byte[] NALUs
```

**Implementation**:
```csharp
// Sử dụng IMFDXGIDeviceManager để share D3D11 device
var mfManager = new MFDXGIDeviceManager();
mfManager.ResetDevice(d3dDevice, resetToken);

// Configure MFT (Media Foundation Transform) H.264 encoder
var encoder = MFTransform.Create(CLSID_MSH264EncoderMFT);
encoder.SetInputType(0, inputType);  // NV12 texture input
encoder.SetOutputType(0, outputType); // H.264 bitstream

// Zero-copy encode
encoder.ProcessInput(0, sample); // sample wraps ID3D11Texture2D
encoder.ProcessOutput(0, outputSample);
```

**Lợi ích**:
- Loại bỏ GPU→CPU copy (~15ms saved)
- Hardware encode luôn khả dụng (MF fallback to software)
- Tích hợp tốt với Windows

**Nhược điểm**:
- Media Foundation API phức tạp
- Windows-only
- Vẫn cần convert BGRA→NV12 (có thể dùng GPU shader)

### 2.2 Sử Dụng FFmpeg GPU Filter

**Hiện tại**: BGRA raw qua stdin → FFmpeg convert → Encode
**Cải tiến**: Sử dụng hardware upload và convert

```bash
# Thay vì:
-f rawvideo -pix_fmt bgra -i - -c:v h264_nvenc

# Dùng:
-hwaccel d3d11va -hwaccel_output_format d3d11 \
-f rawvideo -pix_fmt bgra -i - \
-vf "hwupload_cuda,scale_cuda=format=nv12" \
-c:v h264_nvenc
```

**Vấn đề**: stdin vẫn phải đi qua CPU, không thể tận dụng được.

### 2.3 Shared Memory IPC Thay Vì Pipe

```csharp
// Sử dụng MemoryMappedFile để share frame buffer
using var mmf = MemoryMappedFile.CreateNew("FrameBuffer", frameSize);
using var accessor = mmf.CreateViewAccessor();

// Capture ghi trực tiếp vào shared memory
accessor.WriteArray(0, frameData, 0, frameData.Length);

// FFmpeg đọc từ shared memory (cần custom input protocol)
```

**Lợi ích**: Giảm ~5ms copy overhead
**Nhược điểm**: FFmpeg không hỗ trợ native, cần custom build

### 2.4 Adaptive Quality System

```csharp
public class AdaptiveEncoder
{
    public EncoderProfile SelectProfile(HardwareCapability hw)
    {
        if (hw.HasNvenc && hw.NvencGeneration >= 7)
            return new EncoderProfile("h264_nvenc", "p4", 0, 23); // Low latency
        
        if (hw.HasQsv && hw.QsvGeneration >= 8)
            return new EncoderProfile("h264_qsv", "veryfast", 0, 25);
        
        if (hw.HasAmf)
            return new EncoderProfile("h264_amf", "speed", 0, 25);
        
        // CPU fallback with reduced quality
        return new EncoderProfile("libx264", "ultrafast", 2000, -1)
        {
            MaxResolution = (1280, 720),
            MaxFps = 24
        };
    }
}
```

---

## 3. Giải Pháp Đổi Nền Tảng

### 3.1 Rust + WebRTC Native

**Stack**:
- `webrtc-rs` hoặc `libwebrtc` binding
- `wgpu` cho GPU compute
- `windows-rs` cho Windows API

```rust
// Capture với win32 API trực tiếp
let duplication = output.duplicate_output(&device)?;

// Encode với hardware codec
let encoder = nvenc::Encoder::new(&device)?;

// WebRTC native
let peer = RTCPeerConnection::new(config).await?;
let video_track = peer.add_video_track(codec).await?;

// Zero-copy pipeline
loop {
    let frame = duplication.acquire_next_frame()?;
    let encoded = encoder.encode_texture(frame.texture)?;
    video_track.write_sample(encoded).await?;
}
```

**Ưu điểm**:
- Memory-safe, high performance
- Native GPU access
- Cross-platform potential (với adaptation)
- Có ecosystem tốt: `nvcodec-rs`, `webrtc-rs`

**Nhược điểm**:
- Learning curve cao
- Ecosystem chưa mature bằng C++
- Cần rewrite toàn bộ

**Ước tính effort**: 2-4 tháng cho 1 developer

### 3.2 C++ + libwebrtc Native

**Stack**:
- Google libwebrtc (WebRTC native implementation)
- DirectX 11/12 capture
- NVENC/AMF SDK trực tiếp

```cpp
// Native NVENC encoding
NV_ENC_INITIALIZE_PARAMS initParams = {};
nvEncInitializeEncoder(encoder, &initParams);

// Register D3D11 resource
NV_ENC_REGISTER_RESOURCE regRes = {};
regRes.resourceToRegister = d3dTexture;
nvEncRegisterResource(encoder, &regRes);

// Encode without CPU copy
NV_ENC_PIC_PARAMS picParams = {};
nvEncEncodePicture(encoder, &picParams);

// Send via libwebrtc
webrtc::VideoFrame frame = webrtc::VideoFrame::Builder()
    .set_video_frame_buffer(buffer)
    .build();
video_track->SendFrame(frame);
```

**Ưu điểm**:
- Hiệu năng tối ưu nhất
- Trực tiếp control hardware
- libwebrtc là implementation chuẩn

**Nhược điểm**:
- C++ complexity
- libwebrtc build rất phức tạp
- Memory safety issues

**Ước tính effort**: 3-6 tháng

### 3.3 Go + Pion WebRTC

**Stack**:
- Pion WebRTC (pure Go implementation)
- CGo binding cho NVENC/capture
- Simpler architecture

```go
// WebRTC setup
peerConnection, _ := webrtc.NewPeerConnection(config)
videoTrack, _ := webrtc.NewTrackLocalStaticSample(
    webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeH264},
    "video", "stream",
)

// Capture và encode qua CGo
for {
    frame := capture.AcquireFrame()
    encoded := encoder.EncodeFrame(frame)
    videoTrack.WriteSample(media.Sample{Data: encoded})
}
```

**Ưu điểm**:
- Simple codebase
- Good concurrency model
- Pion rất mature

**Nhược điểm**:
- CGo overhead cho GPU operations
- Không có native GPU capture trong Go
- Still need native code for capture/encode

**Ước tính effort**: 1-2 tháng (nếu đã có CGo bindings)

---

## 4. Giải Pháp Kiến Trúc Mới

### 4.1 GPU-Only Pipeline (DirectX Video)

```
┌──────────────┐    ┌──────────────┐    ┌───────────────┐    ┌────────┐
│ DXGI Desktop │───▶│ D3D11 Video  │───▶│ Video Encoder │───▶│ WebRTC │
│ Duplication  │    │ Processor    │    │ (NVENC/QSV)   │    │        │
└──────────────┘    └──────────────┘    └───────────────┘    └────────┘
    GPU Texture        GPU (BGRA→NV12)      GPU                CPU
         │                   │                 │                  │
         └───────────────────┴─────────────────┘                  │
                    ZERO CPU COPY                                 │
                                                                  │
                                            ┌─────────────────────┘
                                            │ Only final bitstream
                                            │ touches CPU memory
```

**Implementation với D3D11 Video Processor**:
```cpp
// Create video processor for color conversion
D3D11_VIDEO_PROCESSOR_CAPS caps;
videoDevice->CreateVideoProcessor(enumerator, 0, &videoProcessor);

// Convert BGRA→NV12 entirely on GPU
D3D11_VIDEO_PROCESSOR_STREAM stream = {};
stream.Enable = TRUE;
stream.pInputSurface = bgraView;
videoContext->VideoProcessorBlt(videoProcessor, nv12View, 0, 1, &stream);

// Feed NV12 texture directly to NVENC
// No CPU copy at any point
```

**Kết quả dự kiến**: Latency giảm từ ~50ms xuống ~15ms

### 4.2 Split-Screen Region Encoding

Cho máy yếu, chia screen thành regions và encode riêng:

```
┌─────────────────────────────────────┐
│  Region 1   │  Region 2   │ Region 3│
│  (Active)   │  (Static)   │(Static) │
│  30fps      │  5fps       │ 5fps    │
│  High Q     │  Low Q      │ Low Q   │
└─────────────────────────────────────┘
```

**Logic**:
- Detect active region (mouse, window changes)
- Encode active region ở high fps/quality
- Background regions encode ở low fps
- Merge ở client side

### 4.3 Hybrid Cloud Encoding

```
┌──────────┐    ┌─────────────┐    ┌───────────────┐
│ Capture  │───▶│ Local GPU   │───▶│ Direct Stream │ (Có GPU mạnh)
│          │    │ Encode      │    │               │
└──────────┘    └─────────────┘    └───────────────┘
     │
     │ (Nếu không có GPU encoder)
     ▼
┌──────────┐    ┌─────────────┐    ┌───────────────┐
│ Compress │───▶│ Cloud       │───▶│ Stream back   │
│ & Upload │    │ Transcode   │    │ to Client     │
└──────────┘    └─────────────┘    └───────────────┘
```

**Concept**: Khi PC yếu, upload raw/lightly-compressed frames lên cloud, encode ở đó rồi stream về client.

**Vấn đề**: Tăng latency network, cần bandwidth cao upload

---

## 5. So Sánh & Đề Xuất

### 5.1 Bảng So Sánh

| Giải Pháp | Độ Khó | Thời Gian | Latency Giảm | Phù Hợp PC Yếu |
|-----------|--------|-----------|--------------|----------------|
| Zero-copy MF (.NET) | Trung bình | 2-4 tuần | 40% | Trung bình |
| Adaptive Quality | Thấp | 1 tuần | 20% | Tốt |
| Rust rewrite | Cao | 2-4 tháng | 60% | Tốt |
| C++ libwebrtc | Rất cao | 3-6 tháng | 70% | Tốt |
| Go + Pion | Trung bình | 1-2 tháng | 40% | Trung bình |
| GPU-only pipeline | Cao | 1-2 tháng | 60% | N/A (cần GPU) |
| Split-region | Trung bình | 2-3 tuần | 30% | Rất tốt |

### 5.2 Đề Xuất Theo Ưu Tiên

#### Ngắn Hạn (1-2 tuần)
1. **Adaptive Quality System**
   - Auto-detect hardware capability
   - Giảm resolution/fps cho máy yếu
   - Không cần thay đổi kiến trúc

2. **Optimize FFmpeg Parameters**
   - Tune `-preset` và `-rc-lookahead`
   - Adjust buffer sizes
   - Profile-specific settings

#### Trung Hạn (1-2 tháng)
3. **Media Foundation Direct Encoding**
   - Bypass FFmpeg pipe
   - Native Windows hardware encoder access
   - Giữ nguyên capture code

4. **Split-Region Encoding**
   - Implement cho CPU-only machines
   - Focus resources on active areas

#### Dài Hạn (3+ tháng)
5. **Rust Rewrite** (Đề xuất mạnh)
   - Modern, safe, performant
   - Better ecosystem cho low-level GPU
   - Future-proof

**HOẶC**

6. **C++ Core + .NET Wrapper**
   - Native capture/encode module
   - Keep .NET for signaling/REST
   - Best of both worlds

---

## 6. Quick Win: Cải Tiến Có Thể Làm Ngay

### 6.1 Tối Ưu FfmpegPipeEncoder

```csharp
// Hiện tại: Ghi từng frame synchronously
_stdin.Write(bgra);

// Cải tiến: Async write với dedicated thread
private readonly BlockingCollection<byte[]> _writeQueue = new(2);

// Writer thread
void WriteLoop() {
    foreach (var frame in _writeQueue.GetConsumingEnumerable()) {
        _stdin.Write(frame);
        ArrayPool<byte>.Shared.Return(frame);
    }
}
```

### 6.2 Reduce Memory Copies

```csharp
// Hiện tại trong DxgiCapture:
for (int y = 0; y < _height; y++)
    Marshal.Copy(src + y * mapped.RowPitch, _buffer, y * _stride, _stride);

// Cải tiến: Bulk copy khi stride khớp
if (mapped.RowPitch == _stride) {
    Marshal.Copy(mapped.DataPointer, _buffer, 0, _buffer.Length);
} else {
    // Existing row-by-row copy
}
```

### 6.3 Frame Skip Intelligence

```csharp
// Skip frames khi encoder đang behind
if (_auChan.Reader.Count > 3) {
    Console.WriteLine("[Encoder] Backpressure detected, skipping frame");
    return;
}
```

---

## 7. Implementation Status (Media Foundation)

### 7.1 Đã Implement

| File | Mô tả |
|------|-------|
| `MFInterop.cs` | P/Invoke declarations cho Media Foundation APIs |
| `MediaFoundationH264Encoder.cs` | Full IMFTransform-based H.264 encoder + EncodeNv12Texture |
| `WebRTCStreamer_MF.cs` | WebRTC streamer sử dụng MF encoder |
| `WebRTCStreamer_ZeroCopy.cs` | **Zero-copy GPU pipeline streamer** |
| `D3D11VideoProcessorGpu.cs` | **GPU-based BGRA→NV12 conversion** |
| `D3D11VideoProcessor.cs` | CPU fallback BGRA→NV12 converter |
| `DxgiCapture.cs` | Enhanced với `OnTextureFrame` event |
| `EncoderFactory.cs` | Factory với ZeroCopy support |

### 7.2 Tính Năng

#### Media Foundation Encoder
- **Hardware Detection**: Tự động tìm hardware H.264 encoder (NVENC, QSV, AMF)
- **DXGI Device Manager**: Integration cho GPU sharing
- **Low Latency Mode**: Enabled khi encoder hỗ trợ
- **Baseline Profile**: Tương thích WebRTC

#### Zero-Copy Pipeline (NEW!)
- **D3D11 Video Processor**: GPU color conversion BGRA→NV12
- **MFCreateDXGISurfaceBuffer**: Encode trực tiếp từ texture
- **No CPU Memory Copy**: Frame data không bao giờ chạm CPU

### 7.3 Sử Dụng

#### Single Monitor
```bash
# Auto-detect (MF hardware encoder nếu có, fallback FFmpeg)
ws://host:8288/signal?mid=0

# Force FFmpeg
ws://host:8288/signal?mid=0&encoder=ffmpeg

# Force Media Foundation (CPU copy)
ws://host:8288/signal?mid=0&encoder=mf

# Force Zero-Copy GPU Pipeline (LOWEST LATENCY!)
ws://host:8288/signal?mid=0&encoder=zerocopy
ws://host:8288/signal?mid=0&encoder=zc
```

#### Cluster Mode (Multi-Monitor)
```bash
# Cluster với auto-detect encoder
ws://host:8288/signal?mode=cluster

# Cluster với Zero-Copy GPU Pipeline (LOWEST LATENCY!)
ws://host:8288/signal?mode=cluster&encoder=zerocopy
ws://host:8288/signal?mode=cluster&encoder=zc

# Cluster với các encoder khác
ws://host:8288/signal?mode=cluster&encoder=mf
ws://host:8288/signal?mode=cluster&encoder=ffmpeg
```

### 7.4 So Sánh Latency

| Pipeline | Latency Ước Tính | GPU Memory | CPU Memory |
|----------|------------------|------------|------------|
| DXGI → FFmpeg pipe → WebRTC | ~50-70ms | Read | Copy |
| DXGI → MF Hardware → WebRTC | ~20-35ms | Read | Copy |
| **DXGI → Zero-Copy → WebRTC** | **~10-20ms** | **GPU Only** | **None** |

### 7.5 Zero-Copy Pipeline Flow

#### Single Monitor
```
┌─────────────────┐     ┌─────────────────────┐     ┌──────────────┐     ┌────────┐
│ DXGI Desktop    │────▶│ D3D11 Video         │────▶│ MF H.264     │────▶│ WebRTC │
│ Duplication     │     │ Processor           │     │ Encoder      │     │ RTP    │
│ (BGRA Texture)  │     │ (BGRA→NV12 on GPU)  │     │ (GPU Encode) │     │        │
└─────────────────┘     └─────────────────────┘     └──────────────┘     └────────┘
        │                        │                         │
        └────────────────────────┴─────────────────────────┘
                    GPU MEMORY ONLY - NO CPU COPY
```

#### Cluster Mode (Multi-Monitor)
```
┌──────────────┐
│ Monitor 0    │──┐
│ DXGI Dup     │  │
└──────────────┘  │     ┌───────────────────┐     ┌─────────────────────┐
                  ├────▶│ Combined Texture  │────▶│ D3D11 Video         │
┌──────────────┐  │     │ (CopySubresource  │     │ Processor           │
│ Monitor 1    │──┤     │  Region on GPU)   │     │ (BGRA→NV12 on GPU)  │
│ DXGI Dup     │  │     └───────────────────┘     └─────────────────────┘
└──────────────┘  │                                        │
                  │                                        ▼
┌──────────────┐  │                               ┌──────────────┐     ┌────────┐
│ Monitor N    │──┘                               │ MF H.264     │────▶│ WebRTC │
│ DXGI Dup     │                                  │ Encoder      │     │ RTP    │
└──────────────┘                                  │ (GPU Encode) │     │        │
                                                  └──────────────┘     └────────┘
        ALL COMPOSITING AND ENCODING ON GPU - NO CPU COPY
```

---

## 8. Kết Luận

Hệ thống hiện tại có latency cao do:
1. **CPU memory roundtrip** - có thể giảm bằng zero-copy pipeline
2. **FFmpeg pipe overhead** - có thể thay bằng Media Foundation
3. **Thiếu adaptive quality** - cần implement cho máy yếu

**Khuyến nghị lộ trình**:
1. **Tuần 1-2**: Implement adaptive quality + optimize FFmpeg params
2. **Tháng 1**: Migrate sang Media Foundation direct encoding
3. **Tháng 2-3**: Evaluate Rust rewrite hoặc native C++ module

Với máy không có GPU encoder mạnh, latency ~100ms+ là không thể tránh khỏi với CPU encoding. Giải pháp tốt nhất là:
- Giảm resolution xuống 720p hoặc thấp hơn
- Giảm fps xuống 24
- Hoặc yêu cầu minimum hardware spec có hỗ trợ hardware encoding
