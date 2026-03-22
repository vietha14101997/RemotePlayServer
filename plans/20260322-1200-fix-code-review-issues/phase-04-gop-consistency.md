# Phase 4: GOP Consistency - Fix AmfSetFps

## Context

`AmfSetFps` (AmfWrapper.cpp lines 548-590) overrides the infinite GOP configured at init time:

**Init config (HEVC)**:
- Line 90: `AMF_VIDEO_ENCODER_HEVC_GOP_SIZE = 0` (infinite GOP)
- Line 89: `AMF_VIDEO_ENCODER_HEVC_NUM_GOPS_PER_IDR = 1`

**Init config (H264)**:
- Line 62: `AMF_VIDEO_ENCODER_IDR_PERIOD = fps * 5` (5-second GOP)

**AmfSetFps overrides**:
- Line 575: `HEVC_GOP_SIZE = fps * 2` (overrides infinite GOP with 2-second GOP!)
- Line 584: `IDR_PERIOD = fps * 2` (overrides 5-second GOP with 2-second GOP)
- Lines 577, 585: Forces IDR frame on FPS change

This is inconsistent: init uses infinite/5s GOP for low-latency (relying on intra refresh), but FPS change suddenly switches to 2s GOP with forced IDR. The forced IDR is especially problematic -- it causes a large frame spike that can trigger SCTP congestion (same death spiral documented in NvencWrapper's `BuildReconfigParams` comments, line 653-656).

## Overview

Fix `AmfSetFps` to maintain the init-time GOP strategy: keep infinite GOP for HEVC and 5-second GOP for H264. Remove forced IDR on FPS change. Match NVENC behavior where `BuildReconfigParams` explicitly sets `forceIDR = 0`.

## Key Insights

1. NVENC already does this right: `BuildReconfigParams` line 655 sets `reconfigParams.forceIDR = 0` with explicit comment about IDR death spiral
2. AMF HEVC init uses GOP_SIZE=0 (infinite) + intra refresh for gradual quality recovery. Switching to GOP_SIZE=fps*2 defeats this design.
3. For H264, init uses `IDR_PERIOD = fps * 5`. Changing to fps*2 on FPS change doubles IDR frequency unnecessarily.
4. Forcing IDR on FPS change is unnecessary -- the encoder adapts frame timing internally. If a keyframe is needed, the C# layer explicitly calls `forceKeyframe=true`.

## Requirements

- `AmfSetFps` HEVC: only update FRAMERATE, do NOT change GOP_SIZE or force IDR
- `AmfSetFps` H264: only update FRAMERATE and scale IDR_PERIOD proportionally to match init-time 5-second interval
- Add comment explaining why IDR is not forced (reference NVENC consistency)
- Update intra refresh parameters to match new FPS (CTBs/MBs per slot)

## Related Code Files

| File | Lines | Action |
|------|-------|--------|
| `Native/AmfWrapper/AmfWrapper.cpp` | 548-590 | MODIFY: fix GOP + remove forced IDR |

## Implementation Steps

### Step 1: Fix HEVC branch (lines 569-577)
Replace:
```cpp
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_GOP_SIZE, fps * 2);
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_INSERT_HEADER, true);
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_HEVC_PICTURE_TYPE_IDR);
```
With:
```cpp
// Keep infinite GOP (GOP_SIZE=0) — matches init config. Intra refresh handles
// quality recovery without IDR frame spikes. Do NOT force IDR on FPS change:
// large IDR can trigger SCTP congestion death spiral (see NvencWrapper comments).

// Update intra refresh to match new FPS (refresh entire frame in ~1 second)
int ctbCols = (ctx->width + 63) / 64;
int ctbRows = (ctx->height + 63) / 64;
int totalCtbs = ctbCols * ctbRows;
int ctbsPerSlot = (totalCtbs + fps - 1) / fps;
if (ctbsPerSlot < 1) ctbsPerSlot = 1;
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_INTRA_REFRESH_NUM_CTBS_PER_SLOT, (amf_int64)ctbsPerSlot);
```

### Step 2: Fix H264 branch (lines 578-585)
Replace:
```cpp
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_IDR_PERIOD, fps * 2);
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR);
```
With:
```cpp
// Maintain 5-second IDR period (matches init config), scaled to new FPS.
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_IDR_PERIOD, fps * 5);

// Update intra refresh to match new FPS
int mbCols = (ctx->width + 15) / 16;
int mbRows = (ctx->height + 15) / 16;
int totalMbs = mbCols * mbRows;
int mbsPerSlot = (totalMbs + fps - 1) / fps;
if (mbsPerSlot < 1) mbsPerSlot = 1;
ctx->encoder->SetProperty(AMF_VIDEO_ENCODER_INTRA_REFRESH_NUM_MBS_PER_SLOT, (amf_int64)mbsPerSlot);
```

## Todo

- [ ] Remove `GOP_SIZE = fps * 2` from HEVC branch in AmfSetFps
- [ ] Remove `FORCE_PICTURE_TYPE IDR` from both branches in AmfSetFps
- [ ] Keep `IDR_PERIOD = fps * 5` for H264 branch (match init config)
- [ ] Add intra refresh recalculation for both branches
- [ ] Add explanatory comments referencing NVENC consistency
- [ ] Verify DLL builds

## Success Criteria

- `AmfSetFps` no longer overrides GOP strategy set at init time
- No forced IDR on FPS change
- Intra refresh scales correctly with new FPS
- HEVC stays at infinite GOP, H264 stays at 5-second GOP

## Risk Assessment

**Medium risk**. Behavioral change: FPS changes no longer produce an IDR frame. If client needs immediate resync after FPS change, it must explicitly request `forceKeyframe=true` from C# layer. However, the C# layer already handles this via per-track `ForceNextKeyframe` flag when needed.

Mitigating factor: NVENC already works this way (no forced IDR on reconfig) and has been stable in production.
