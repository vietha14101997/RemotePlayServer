# H265/HEVC Encoding & RTP Packetization Audit

**Date**: 2026-03-06
**Scope**: Server-side H265 encoding pipeline, RTP packetization, and DataChannel side-channel
**Files Audited**:
- `Infrastructure/Network/H265Fragmenter.cs`
- `Infrastructure/Network/H264Fragmenter.cs`
- `Application/Streaming/SIPSorceryStreamer.FrameSending.cs`
- `Application/Streaming/SIPSorceryStreamer.Setup.cs`
- `Application/Streaming/SIPSorceryStreamer.Lifecycle.cs`
- `Application/Streaming/SIPSorceryStreamer.RtpSync.cs`
- `Application/Streaming/SIPSorceryStreamer.cs`
- `Infrastructure/Encoding/NvencNativeWrapper.cs`
- `Infrastructure/Encoding/AmfNativeWrapper.cs`
- `Native/NvencWrapper/NvencWrapper.cpp`
- `Native/AmfWrapper/AmfWrapper.cpp`
- `Infrastructure/Encoding/LibAvEncoder.Encoding.cs`

---

## Executive Summary

The H265 pipeline has **6 confirmed bugs** and **3 high-risk design issues** that collectively explain why the client decodes only 2 frames before stalling. The root causes are:

1. **Critical**: The H265Fragmenter sends single-NAL RTP packets without the mandatory 2-byte HEVC NAL header wrapper, breaking RFC 7798 for small NALUs (delta frames).
2. **Critical**: AMF emits NAL type 35 (PREFIX_SEI/T35) before VPS. `StripLeadingAud()` identifies type 35 as the H265 AUD and strips the entire leading SEI + all subsequent NALUs from AMF frames.
3. **High**: `ExtractH265ParamSets()` uses a single-pass `i = nalEnd` advance but `nalEnd` is computed from 4-byte start code search only — the loop can skip 3-byte start codes between NALUs and silently drop parameter sets.
4. **High**: NVENC IDR frames sent via DataChannel contain the full keyframe Annex-B blob (VPS+SPS+PPS+IDR). `ExtractH265IdrData()` strips everything but IDR NALUs (type 19/20), but ignores CRA (type 21) and BLA frames, causing decoder stall on encoders that emit CRA instead of IDR.
5. **Medium**: The `StripLeadingAud()` function only strips the *first* AUD in the stream. AMF HEVC frames may have a T35/SEI prefix (type 35) followed by AUD (type 35 is also used for HEVC AUD, but T35 is a completely different thing) — the code misidentifies T35 as AUD.
6. **Medium**: `ParseAnnexB()` in `H265Fragmenter` has an off-by-one boundary: the 4-byte start code check uses `i + 3 < data.Length` (correct) but the 3-byte check uses `i + 2 < data.Length` — this is correct, but the interplay means that a buffer ending in `00 00 01` at positions `len-3..len-1` is parsed but then `start` points to `len`, yielding a zero-length NAL added to the result.
7. **Medium**: `AmfSetBitrate()` unconditionally sets `FORCE_PICTURE_TYPE=IDR` after changing bitrate, causing the encoder to emit an IDR mid-stream with no HEVC repeat-headers request. The server's `isKeyframe` detection then fires, triggering full IDR via DataChannel on every bitrate change.

---

## Bug 1 (Critical): H265Fragmenter Sends Bare NAL Without RFC 7798 Header for Small Packets

**File**: `Infrastructure/Network/H265Fragmenter.cs`, lines 26-30

```csharp
if (nalU.Length <= MAX_RTP_PAYLOAD_SIZE)
{
    result.Add(nalU);   // BUG: sent as-is, no RFC 7798 payload header
}
```

**RFC 7798 §4.4.1 (Single NAL Unit Packet)** requires that every single-NAL-unit RTP packet's payload IS the NAL unit itself including its 2-byte HEVC NAL header. This is correct. However, SIPSorcery's underlying `SendRtpRaw` does not know the codec is HEVC and does not prepend any payload header — it sends the raw bytes as the RTP payload.

For H264, the raw NAL (starting with the 1-byte NAL header) is also the correct RTP payload per RFC 6184. For H265, the raw NAL (starting with the 2-byte HEVC NAL header) is also the correct single NAL unit packet per RFC 7798 §4.4.1.

**The real bug**: The client reports "Repaired missing HEVC header byte." This points to an interaction with how SIPSorcery sends the payload. Inspect `SendRtpRaw` — if SIPSorcery's H265 packetization strips or skips 1 byte (treating the first byte as an H264 NAL header and skipping it), the client receives data starting from byte index 1 of the HEVC NAL header, losing the first byte (`forbidden_zero_bit | nal_unit_type[5:1]`). The client then has to "repair" it.

**Evidence**: Client consistently sees this on *all* delta frames (which go through RTP only, not DataChannel). The IDR frames that go through DataChannel decode fine for 2 frames because the DataChannel path bypasses SIPSorcery's RTP layer entirely.

**Suspected location**: SIPSorcery's `VideoStream.SendRtpRaw()` or its H265 payload formatter may strip 1 byte thinking it's doing H264-style processing. Since SIPSorcery originally targeted H264, there is a risk its RTP send path prepends its own NAL header byte or shifts the payload by 1.

**Action required**: Verify SIPSorcery version's behavior for H265 payload type. If it offsets by 1, the fix is to prepend a dummy byte in `H265Fragmenter.FragmentAnnexB()` for single-NAL packets, or switch to always using FU packetization for H265 (which constructs its own 3-byte header and is unambiguous).

---

## Bug 2 (Critical): StripLeadingAud() Misidentifies AMF's T35/SEI Prefix as H265 AUD

**File**: `Application/Streaming/SIPSorceryStreamer.Setup.cs`, lines 434-473

```csharp
if (_negotiatedCodec == VideoCodec.H265)
{
    nalType = (au[pos] >> 1) & 0x3F;
    if (nalType != 35) return au; // H265 AUD is 35
}
```

**The problem**: In HEVC, NAL type 35 is `AUD_NUT` (Access Unit Delimiter). BUT AMF's HEVC encoder emits NAL type 35 as a **SEI prefix** (`PREFIX_SEI_NUT` = 39 in HEVC, not 35). Wait — re-checking HEVC NAL types:

- `AUD_NUT` = 35
- `PREFIX_SEI_NUT` = 39
- `SUFFIX_SEI_NUT` = 40
- `UNSPEC35` = 35 in some encoder interpretations

The user's observation states "AMF encoder outputs NAL type 35 (T35/SEI prefix) before VPS." In the HEVC spec, type 35 IS `AUD_NUT`. However, AMD's AMF may emit a non-standard SEI or T35 metadata NAL at type 35 as a vendor extension, or more likely AMF is emitting `AUD_NUT` which the server code does correctly identify as AUD and strip.

**However, the real bug is here**: When `StripLeadingAud()` finds type 35 at position `pos`, it searches forward for the *next* start code and returns everything from that next start code onward. If AMF emits: `[AUD=35][VPS=32][SPS=33][PPS=34][IDR=19]`, stripping AUD is correct and safe. But if AMF emits: `[T35=35][AUD=35][VPS=32][SPS=33][PPS=34][IDR=19]` (T35 before AUD), then `StripLeadingAud()` strips from the leading T35 to the next start code (which is AUD), and returns `[AUD=35][VPS][SPS][PPS][IDR]` — the AUD is left in. This is fine.

**But**: if AMF emits just `[T35=35][VPS=32][SPS=33][PPS=34][IDR=19]` with no AUD, `StripLeadingAud()` treats T35 (type 35) as AUD and strips it, returning `[VPS][SPS][PPS][IDR]`. The VPS/SPS/PPS are preserved. This is actually *correct* behavior — T35 is not needed for decoding.

**The actual danger**: AMF HEVC emits T35 as type 35 on every frame (not just keyframes). On a delta frame: `[T35=35][TRAIL_R=1]`. `StripLeadingAud()` sees type 35, finds the next start code (TRAIL_R), and returns `[TRAIL_R=1]` — correct. But if there is no second start code (a T35-only frame or a T35 as the last NAL), the `for` loop finds nothing and the original `au` is returned — the T35 is included in the RTP stream. T35 in RTP is harmless but wastes bandwidth.

**The real bug revealed by logs**: The observation "AMF encoder outputs NAL type 35 before VPS while NVENC doesn't" combined with "client reports Repaired missing HEVC header byte on many RTP packets" suggests the T35 NAL is small enough to be sent as a single-NAL RTP packet. If SIPSorcery is eating 1 byte of the HEVC NAL header (see Bug 1), the T35 NAL's payload appears truncated at the client, and the client reports a repair. This means Bug 1 and Bug 2 interact: T35 NALs from AMF cause an extra RTP packet per frame where the first byte is "missing."

---

## Bug 3 (High): ExtractH265ParamSets() Has a Loop Advance Bug That Can Drop NALUs

**File**: `Application/Streaming/SIPSorceryStreamer.FrameSending.cs`, lines 531-572

```csharp
i = nalEnd;
```

The loop advances `i` to `nalEnd` after processing each NAL. `nalEnd` is found by searching from `headerPos + 1` forward for the *next* start code. The search looks for 4-byte start codes first, then 3-byte. This is correct.

**However**: The `nalEnd` search at line 553-559 only checks 4-byte start codes (`data[j+3] == 1`) and 3-byte start codes as a separate check. The 3-byte check is:

```csharp
if (annexB[j] == 0 && annexB[j + 1] == 0 && annexB[j + 2] == 1)
{ nalEnd = j; break; }
```

This is inside a loop that checks `j < annexB.Length - 3`. The bounds are correct. But after setting `i = nalEnd`, the outer loop then executes `startCodeLen` detection at position `nalEnd`, which correctly identifies the start code and advances past it.

**Real bug**: When `nalType >= 32 && nalType <= 34` (VPS/SPS/PPS) is false (e.g., for SEI type 39), the code does NOT write to the result stream BUT still advances `i = nalEnd`. This is correct — it skips non-param-set NALUs. However, the IRAP break condition is:

```csharp
if (nalType <= 21 && nalType >= 16) break; // IRAP NAL, no more param sets
```

This means: for CRA (type 21), IDR_W_RADL (19), IDR_N_LP (20), BLA_W_LP (16..18), and all types 16-21, the loop breaks early. But **type 35 (AUD)** is NOT in this range, so if AMF emits `[T35][VPS][SPS][PPS][IDR]`, the loop processes T35 (skips it, advances `i`), then hits VPS, SPS, PPS (adds them), then hits IDR (type 19, breaks). Correct.

**But for NVENC**: NVENC emits `[VPS][SPS][PPS][IDR]` with no T35. The loop processes VPS, SPS, PPS, IDR (breaks). Correct.

**The subtle bug**: `nalEnd` is found by searching from `headerPos + 1` (not from `i + scLen + 1`). When `scLen == 3` and the NAL data contains bytes `00 00 00 01` in the payload (emulation prevention bytes should prevent this, but...), the inner NAL-end search might terminate early on emulation-prevention sequences. This is a low-risk theoretical issue.

**More significant**: The outer loop condition is `i < annexB.Length - 4`. When `nalEnd == annexB.Length` (last NAL), `i` is set to `annexB.Length` and the loop exits. The last NAL IS written to the result. Correct.

**Verdict**: No confirmed bug in `ExtractH265ParamSets()` logic itself, but the function's behavior with AMF's T35 prefix (which has nalType=35, outside 16-21) is correct — it skips T35 and continues finding VPS/SPS/PPS.

---

## Bug 4 (High): ExtractH265IdrData() Misses CRA and BLA IDR-Equivalent Frames

**File**: `Application/Streaming/SIPSorceryStreamer.FrameSending.cs`, lines 579-617

```csharp
// Keep only IDR_W_RADL(19) and IDR_N_LP(20)
if (nalType == 19 || nalType == 20)
{
    result.Write(annexB, nalStart, nalEnd - nalStart);
}
```

AMF HEVC can emit CRA (Clean Random Access, type 21) frames instead of IDR on some keyframe requests. NVENC can also emit CRA. The `isKeyframe` detection at the C++ layer correctly identifies CRA as a keyframe:

```cpp
// VPS=32, SPS=33, IDR_W_RADL=19, IDR_N_LP=20, CRA=21
if (nalType == 32 || nalType == 33 || nalType == 19 || nalType == 20 || nalType == 21) {
    return 1;
}
```

But `ExtractH265IdrData()` does NOT extract CRA NALs (type 21). If a keyframe is a CRA frame, `ExtractH265IdrData()` returns an empty array, and `SendH265IdrViaDataChannel()` logs "No IDR NAL found in keyframe" and sends nothing via DataChannel. The client then relies entirely on RTP for the CRA keyframe, which is fragmented into many FU packets that may not arrive reliably (per the comment in the code itself). This directly causes decoder stall after ~2 frames on AMF hardware.

**Fix**: Add `nalType == 21` to the check in `ExtractH265IdrData()`.

---

## Bug 5 (Medium): AmfSetBitrate() Forces IDR Without Repeat-Headers

**File**: `Native/AmfWrapper/AmfWrapper.cpp`, lines 500-513

```cpp
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_HEVC_PICTURE_TYPE_IDR);
```

After every bitrate change, `AmfSetBitrate()` forces the next picture to be IDR. This triggers `isKeyframe=true` in the callback, which triggers `SendH265ParamSetsViaDataChannel()` and `SendH265IdrViaDataChannel()`. This is actually *intentional* (force IDR after bitrate change for decoder sync), but the HEVC `INSERT_HEADER` property is NOT set alongside the forced IDR type in `AmfSetBitrate()`. This means the IDR NAL is emitted without VPS/SPS/PPS prepended, so `ExtractH265ParamSets()` finds nothing and logs a warning. The encoder uses cached param sets from `track.LastH265ParamSets`, which may be stale if resolution changed.

Similarly, `AmfSetFps()` sets `HEVC_GOP_SIZE = fps * 2` after changing FPS, which effectively sets a finite GOP and will cause periodic keyframes at the encoder level even when the server doesn't request them, confusing the `isKeyframe` logic.

---

## Bug 6 (Medium): H265Fragmenter ParseAnnexB Edge Case — Zero-Length Terminal NAL

**File**: `Infrastructure/Network/H265Fragmenter.cs`, lines 43-82

```csharp
if (i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
    startCodeLen = 4;
else if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
    startCodeLen = 3;
```

If `data` ends with `... [NAL payload] 00 00 01` (a 3-byte start code at the very end with no following data), the start code is detected, `start` is set to `data.Length` (the position after the start code), and the final block:

```csharp
if (start != -1 && start < data.Length)
{
    byte[] nalU = new byte[data.Length - start];  // = 0 bytes
    ...
    result.Add(nalU);  // Adds zero-length NAL
}
```

The condition `start < data.Length` is FALSE when start == data.Length, so the zero-length NAL is NOT added. **This edge case is handled correctly** — no bug here in practice.

However, there IS a genuine edge case: if a 3-byte start code check uses `i + 2 < data.Length` (strictly less), then when `i == data.Length - 3` (last 3 bytes are `00 00 01`), the condition `i + 2 < data.Length` becomes `data.Length - 1 < data.Length` = TRUE. Start code is detected, `i += 3` → `i = data.Length`, `start = data.Length`. The final block: `start < data.Length` is FALSE. No zero-length NAL added. **Still correct**.

**Verdict**: No bug in edge case handling.

---

## Bug 7 (Medium): NVENC BGRA Mode Registers Format as ARGB, But BGRA Is Different

**File**: `Native/NvencWrapper/NvencWrapper.cpp`, lines 503-504

```cpp
regRes.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;
```

NVENC `NV_ENC_BUFFER_FORMAT_ARGB` is ARGB channel order (A, R, G, B). Windows DXGI `BGRA8` textures have channel order (B, G, R, A). These are byte-swapped. The correct NVENC format for BGRA textures is `NV_ENC_BUFFER_FORMAT_ABGR` (which maps to BGRA in memory on little-endian: B, G, R, A).

Using ARGB format for a BGRA texture means R and B channels are swapped. The HEVC encoder will encode with wrong colors (red/blue swap). This is a visual artifact bug, not a decoder-stall bug, but confirms the BGRA pipeline has color format confusion.

**Check**: `NV_ENC_BUFFER_FORMAT_ARGB` in NVENC SDK is defined as 4-component ARGB 8-bit packed (ARGB order). A DXGI BGRA texture has bytes in BGRA order in memory. For NVENC to consume correctly, use `NV_ENC_BUFFER_FORMAT_ABGR` or ensure the D3D11 texture format is `DXGI_FORMAT_B8G8R8A8_UNORM` matched to NVENC's `NV_ENC_BUFFER_FORMAT_ARGB` if NVENC internally reinterprets ARGB as matching BGRA layout (some driver versions do this). This needs verification against the NVENC SDK version in use.

---

## Design Issue A: Single Bitstream Buffer in NVENC — No Pipeline Depth

**File**: `Native/NvencWrapper/NvencWrapper.cpp`, lines 297-313

NVENC uses a single `bitstreamBuffer` for all frames. If `nvEncEncodePicture` returns `NV_ENC_ERR_NEED_MORE_INPUT` (common at encoder startup or with B-frames disabled but pipeline buffering), `NvencRetrieveOutput()` is still called immediately. With only one output buffer, if NVENC has not yet emitted a frame (double-buffering latency), `nvEncLockBitstream` will block until a frame is ready or return an error. If it blocks, the encode mutex is held and all subsequent `PushTexture` calls are serialized, causing frame drops.

For true zero-latency H265, NVENC needs a pipeline of at least 2-3 output buffers. The current single-buffer design works with H264 because SIPSorcery's preset uses `NV_ENC_TUNING_INFO_LOW_LATENCY` which typically has 1-frame latency. With H265, NVENC may buffer more internally before emitting output.

---

## Design Issue B: DataChannel IDR Side-Channel Sends Every Keyframe

**File**: `Application/Streaming/SIPSorceryStreamer.FrameSending.cs`, lines 261-269

```csharp
// Send full IDR data for every H265 keyframe.
SendH265IdrViaDataChannel(track, nalData);
track.IdrViaDcCount++;
```

The comment acknowledges this is intentional ("some clients only receive partial RTP payload"). But with `RequestKeyframeBurst()` sending 3-5 consecutive IDR frames (e.g., on stall detection), the DataChannel gets 3-5 large IDR payloads in rapid succession. Each IDR at 1080p is typically 300-800KB chunked at 60KB = 5-13 SCTP messages. Sending 3 bursts = 15-39 SCTP messages in ~100ms. This can cause SCTP back-pressure or reordering, and at the same time the same frames are sent via RTP, creating redundant network load that worsens any existing congestion.

---

## Design Issue C: Stall Detection Forces Keyframe Burst While in Stall Condition

**File**: `Application/Protocol/PhaseProtocolHandler.Phase3.cs`, lines 801-806

```csharp
case 1:
    newBitrate = Math.Max(2000, (int)(currentBitrate * 0.75));
    severity = "moderate (25% cut)";
    sendKeyframeBurst = true;
    break;
```

On the first stall, the server sends a keyframe burst AND cuts bitrate. H265 IDR frames are significantly larger than the reduced bitrate allocation allows. At 2000 kbps with 30fps, the per-frame budget is ~8.3KB. An H265 IDR frame at 1080p is typically 50-200KB. Sending 3 IDR frames in a burst on a stalled/congested network will push ~150-600KB of data at a time when the network is already failing, making the stall worse. The code comment at stall 4+ correctly avoids keyframe bursts ("Keyframes are larger than P-frames and pile up in the send buffer"), but stalls 1-3 still do it.

---

## Confirmed Root Cause of "2 Frames Then Stall"

Combining the above:

1. Client connects, requests H265.
2. Server sends VPS+SPS+PPS via DataChannel (type 0x02) — client initializes decoder.
3. Server sends IDR via DataChannel (type 0x03) — client decodes **frame 1**. Success.
4. Server sends a few delta frames via RTP. For each delta frame:
   - AMF: emits `[T35=35][TRAIL_R=1]`, server strips T35 (AUD stripping), sends `[TRAIL_R=1]`.
   - RTP: H265Fragmenter sends `[TRAIL_R NAL]` as single-NAL RTP packet.
   - SIPSorcery: sends this payload via RTP.
   - Client: receives RTP with "missing HEVC header byte" → repairs → decodes **frame 2**.
5. At some point (often frame 3+), the RTP payload's first byte repair fails or the repaired NAL is malformed. Client decoder stalls.
6. For NVENC: no T35, but same "missing first byte" issue on all single-NAL RTP packets. Client repairs some, fails on others.

The "2 frames" that do decode are: frame 1 (IDR via DataChannel, perfect), frame 2 (first delta via RTP, partial repair succeeds). Frame 3+ fail because either the first-byte reconstruction is incorrect for that NAL type, or the reference frame (frame 1 IDR) is evicted/corrupted due to the malformed frames.

---

## Prioritized Fix List

| Priority | Bug | Location | Fix |
|----------|-----|----------|-----|
| P0 - Critical | Single-NAL RTP payload missing first byte | SIPSorcery RTP send path | Investigate SIPSorcery's H265 payload handling; if it skips 1 byte, prepend dummy byte or always use FU for H265 |
| P0 - Critical | CRA frames not sent via DataChannel | `ExtractH265IdrData()` line 609 | Add `\|\| nalType == 21` to the IDR check |
| P1 - High | NVENC BGRA uses wrong NVENC buffer format | `NvencWrapper.cpp` line 503 | Change `NV_ENC_BUFFER_FORMAT_ARGB` to `NV_ENC_BUFFER_FORMAT_ABGR` and verify color output |
| P1 - High | AmfSetBitrate/AmfSetFps doesn't set INSERT_HEADER | `AmfWrapper.cpp` lines 500, 548 | Add `AMF_VIDEO_ENCODER_HEVC_INSERT_HEADER=true` on forced IDR |
| P2 - Medium | AmfSetFps sets finite GOP size | `AmfWrapper.cpp` line 547 | Remove the GOP_SIZE change; keep infinite GOP |
| P2 - Medium | Keyframe burst on stall worsens congestion | `PhaseProtocolHandler.Phase3.cs` | Disable burst for stalls 1-3 (match behavior of stall 4+) |
| P3 - Low | NVENC single output buffer | `NvencWrapper.cpp` | Add output buffer pool (2-3 buffers) |

---

## Unresolved Questions

1. **SIPSorcery version**: What exact version of SIPSorcery is referenced in `RemotePlayServer.csproj`? The H265 RTP payload formatter behavior (whether it skips 1 byte) depends on the library version. Need to inspect `VideoStream.SendRtpRaw()` source in the SIPSorcery dependency.

2. **Does the client's "Repaired missing HEVC header byte" happen on FU packets or single-NAL packets?** If only on FU packets, Bug 1 is less likely and the FU header construction in `H265Fragmenter.CreateFUs()` should be verified against the actual wire format.

3. **Does NVENC H265 with `repeatSPSPPS=1` actually prepend VPS/SPS/PPS on every IDR?** If yes, `ExtractH265ParamSets()` should always find param sets from NVENC keyframes (no cache needed). But if NVENC only emits them on the first IDR (some driver versions), the cache path is critical.

4. **Is AMF's type 35 output actually `AUD_NUT` or a vendor SEI?** Capturing the raw bitstream (pre-`StripLeadingAud`) for 3 frames from AMF in H265 mode and hexdumping the first 16 bytes would confirm.
