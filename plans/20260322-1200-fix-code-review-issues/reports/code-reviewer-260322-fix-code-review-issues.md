# Code Review Report

**Plan**: 20260322-1200-fix-code-review-issues
**Date**: 2026-03-22
**Reviewer**: code-reviewer

---

## Scope

- Files reviewed: 13 (all 14 listed; `LibAvEncoderAdapter.cs` merged into scope)
- Review focus: All 5 phases — correctness of implementation vs. plan requirements
- Updated plans: plan.md (task status)

---

## Overall Assessment

All 5 phases are implemented and functionally correct for the mainline paths. Three issues require attention before this is closed out.

---

## Critical Issues

None.

---

## High Priority Findings

### H1 — CHECK_HR macro leaks resources on `BEGIN_STREAMING` / `START_OF_STREAM` failure (QsvWrapper.cpp lines 330–333)

`CHECK_HR` macro calls `return QSV_WRAPPER_FAIL` directly. At this point `ctx->encoder`, `ctx->deviceManager`, `ctx->d3dDevice`, `ctx->d3dContext`, and now `ctx->codecApi` are all initialized — but none are released on early exit.

```cpp
hr = ctx->encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
CHECK_HR(hr, "BEGIN_STREAMING failed");   // <- leaks encoder, deviceManager, devices, codecApi
```

Same pattern exists earlier (lines 238–243 for `MFCreateDXGIDeviceManager` and `ResetDevice`), though those fire before `ctx->encoder` exists. The newly added `ctx->stagingTexture` and `ctx->codecApi` make the post-SetInputType path more expensive to leak.

Fix: Replace bare `CHECK_HR` after encoder creation with explicit cleanup, or move all COM objects into RAII wrappers. The SetInputType failure block (lines 295–307) correctly releases, but `CHECK_HR` calls after that do not.

**Impact**: Native resource leak on rare init failure path. Not a crash, but causes handle exhaustion over repeated encoder restarts (e.g., fallback → retry cycles).

### H2 — `_callbackBuffer` reuse strategy grows to exact `size`, not 2x (all 3 NativeWrappers)

The plan (phase-05) specified `new byte[(int)size * 2]` (grow with 2x headroom) to minimize repeated allocations as frame sizes fluctuate around a stable maximum. The implementation grows to exact `size`:

```csharp
if (len > _callbackBuffer.Length)
    _callbackBuffer = new byte[len];  // exact, no headroom
```

This means a frame that oscillates between 49999 and 50001 bytes reallocates every other call. Small variance around the stable maximum causes repeated LOH allocations.

Fix: `_callbackBuffer = new byte[len * 2]` or `new byte[len + (len >> 1)]`. One-liner change, all 3 wrappers.

---

## Medium Priority Findings

### M1 — LibAvEncoder.Encoding.cs still allocates `new byte[_packet->size]` per frame

Phase 5 updated the 3 native wrappers to reusable buffers, but `LibAvEncoder.Encoding.cs` (which fires the same `OnEncodedData` event) allocates a fresh `byte[]` at lines 186, 279, 471, 517. This is out of scope per the plan, but the plan's "check LibAvEncoderAdapter" step (Step 5) implicitly covers it.

The `LibAvEncoder` operates under `_lock` per call, so the same reusable-buffer pattern is safe. Without fixing this, the GC pressure improvement from Phase 5 is partial — the LibAv path (FFmpeg fallback) still allocates per frame.

Not blocking for current plan scope, but should be tracked.

### M2 — `nalData.ToArray()` allocation in `SIPSorceryStreamer.OnEncodedData` (line 340)

The plan acknowledges this as an acceptable tradeoff: "Materialize ArraySegment to byte[] for send infrastructure." This is correct — SIPSorcery's `SendRtp` and DataChannel `Send` APIs require `byte[]`, and no span-accepting overload exists. The cost is one allocation per sent frame (drops are filtered before this line), which is a significant improvement over the previous per-frame allocation in the callback.

No action needed. Documented here for completeness.

### M3 — QSV `outputBuffer` oversized for BGRA mode

Line 349: `ctx->outputBuffer.resize(width * height * (useBgra ? 4 : 2))`

For BGRA mode, 4 bytes/pixel is correct for the input staging texture. But the output of the encoder is H.264/H.265 bitstream — never `width * height * 4` bytes. The output buffer size being 4x the NV12 size is overly conservative but not harmful (just wastes ~2x memory vs. NV12 output). The QsvEncodeTexture function creates a `MFCreateMemoryBuffer` with `ctx->outputBuffer.size()` so this is the bitstream output buffer.

A 1440p BGRA frame: 2560x1440x4 = ~14.7MB reserved as output buffer. This is far more than any compressed H.264 frame needs. For H.264/H.265 at streaming bitrates, output is ~5-50KB per frame.

Low-severity waste. Consider capping at e.g. `width * height` (YUV420 uncompressed) which is a safe upper bound for any compressed stream.

---

## Low Priority Suggestions

### L1 — `NalUtils.h` scan loop has off-by-one on last start code

`DetectKeyframeH264`: loop condition `i + 4 < size` means the function checks `data[i+4]` (the NAL type byte). For H.265: `i + 5 < size` (uses `data[i+4]` and `data[i+5]` implicitly via `>> 1`). Both are correct as written. No bug.

### L2 — NVENC `NvencRetrieveOutput` calls callback while holding `encodeMutex`

The callback fires synchronously inside the lock (via `NvencEncodeTexture` → `NvencRetrieveOutput`). The C# `NativeCallback` is called from within native `encodeMutex` — this is the serialization guarantee Phase 5 relies on. However, if `NativeCallback` ever calls back into a wrapper method (e.g. `SetBitrate`) it would deadlock. The comment in `FrameSending.cs` line 847 (`"OnEncodedData runs from WITHIN EncodeLock → calling SetBitrate here causes [deadlock]"`) confirms awareness of this constraint.

No fix needed now, but the constraint should be documented in `NvencWrapper.cpp` near `NvencRetrieveOutput` for future maintainers.

### L3 — QSV ProcessOutput does not handle `MF_E_TRANSFORM_NEED_MORE_INPUT`

`QsvEncodeTexture` lines 494–509: if `ProcessOutput` returns `MF_E_TRANSFORM_NEED_MORE_INPUT`, the function falls through to `SafeRelease` cleanly and returns `QSV_WRAPPER_OK` (since `SUCCEEDED(hr)` is false). This is correct for single-frame operation. No bug; just worth noting that the MFT may buffer internally.

---

## Phase Completion Status

| Phase | Status | Notes |
|-------|--------|-------|
| 1 — DRY NalUtils.h | DONE | NalUtils.h created, included by all 3 wrappers, local copies removed |
| 2 — QSV BGRA support | DONE | MFT ARGB32 path implemented, graceful fail on SetInputType rejection, delegating through QsvEncodeTexture correctly |
| 3 — Clarify comments | DONE | AmfEncodeNV12Bytes has CPU→GPU fallback comment; NvencEncodeTexture has staging copy comment |
| 4 — GOP consistency | DONE | AmfSetFps HEVC keeps GOP_SIZE=0 (infinite), H264 keeps fps*5; IDR not forced; intra refresh recalculated |
| 5 — GC pressure / ArraySegment | DONE | Reusable `_callbackBuffer` in all 3 wrappers; IVideoEncoder event signature changed; FrameSending updated |

All plan todos are completed. Two implementation gaps exist vs. plan intent (H2 buffer growth, M1 LibAv path) that should be addressed in a follow-up.

---

## Positive Observations

- Phase 4 implementation is correct and mirrors NVENC's `BuildReconfigParams` approach. The comment in `AmfSetFps` referencing the NVENC death spiral rationale is exactly right.
- Phase 2 BGRA fallback design is clean: `SetInputType` failure returns `QSV_WRAPPER_FAIL` with a descriptive error message, and `QsvNativeWrapper.InitializeBgra` propagates the failure so the caller can fall back to NV12. No silent degradation.
- The reusable-buffer serialization argument in `AmfNativeWrapper` comment ("callbacks serialized by native encode mutex") is sound and correctly reasoned.
- `QsvEncodeBgraTexture` delegating to `QsvEncodeTexture` (same staging texture, correct format set at init) is simple and correct — no duplication.
- NVENC BGRA cached registration avoids per-frame `Register/Unregister` cost — good performance choice.

---

## Recommended Actions

1. (H1) Fix resource leaks in `QsvCreateEncoderInternal` for `CHECK_HR` paths after `ctx->codecApi` is populated. Add explicit cleanup block similar to the `SetInputType` failure block.
2. (H2) Change reusable buffer growth to 2x: `_callbackBuffer = new byte[len * 2]` in all 3 NativeWrappers. One-line fix.
3. (M1) Track `LibAvEncoder.Encoding.cs` per-frame `byte[]` allocations as a separate follow-up task if the LibAv path is used in production.

---

## Unresolved Questions

1. Does `MFT_MESSAGE_NOTIFY_BEGIN_STREAMING` ever fail in practice on Intel QSV? If so, H1 is production-relevant; otherwise it's a theoretical leak.
2. Phase 5 plan Step 6 ("grep for all OnEncodedData subscribers") — was this grep done? If any subscriber besides `SIPSorceryStreamer` stores the `ArraySegment.Array` across frames, buffer reuse would corrupt data silently.
