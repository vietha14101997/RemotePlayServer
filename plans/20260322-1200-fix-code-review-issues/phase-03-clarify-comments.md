# Phase 3: Clarify Comments

## Context

Two encode functions lack comments explaining non-obvious design decisions:

1. **AmfEncodeNV12Bytes** (AmfWrapper.cpp lines 375-441): Allocates a HOST surface, copies NV12 data row-by-row, then calls `surface->Convert(AMF_MEMORY_DX11)` to upload to GPU. This CPU->GPU path is not zero-copy -- it's a fallback for when callers only have raw bytes (not a D3D11 texture). The function name suggests this is obvious, but the `Convert(AMF_MEMORY_DX11)` call on line 434 is subtle.

2. **NvencEncodeTexture** (NvencWrapper.cpp lines 404-466): Uses `CopyResource` to copy caller's NV12 texture into a pre-registered staging texture (line 422). This is NOT zero-copy -- it's a staging copy required because NVENC needs a registered resource. The BGRA path (`NvencEncodeBgraTexture`) avoids this by caching registration per texture pointer.

## Overview

Add concise inline comments explaining the "why" of these design choices.

## Key Insights

- AmfEncodeNV12Bytes is only used as fallback when no D3D11 texture is available (e.g., software capture). The true zero-copy path is AmfEncodeTexture.
- NvencEncodeTexture staging copy is required by NVENC API: input must be a pre-registered resource. Can't register arbitrary caller textures per-frame (too expensive). BGRA path caches registration to avoid this cost.

## Requirements

- Add comments that explain WHY, not WHAT
- Keep comments concise (2-3 lines max each)
- No code changes

## Related Code Files

| File | Lines | Action |
|------|-------|--------|
| `Native/AmfWrapper/AmfWrapper.cpp` | 374-438 | ADD comment before Convert() call |
| `Native/NvencWrapper/NvencWrapper.cpp` | 420-422 | ADD comment before CopyResource call |

## Implementation Steps

### Step 1: AmfWrapper.cpp - AmfEncodeNV12Bytes
Add before line 434 (`res = surface->Convert(amf::AMF_MEMORY_DX11);`):
```cpp
// CPU->GPU upload: this function is a FALLBACK for raw byte input (e.g., software capture).
// For zero-copy D3D11 texture encoding, use AmfEncodeTexture/AmfEncodeBgraTexture instead.
```

### Step 2: NvencWrapper.cpp - NvencEncodeTexture
Add before line 422 (`ctx->d3dContext->CopyResource(...)`):
```cpp
// Staging copy required: NVENC API requires pre-registered input resources.
// We copy into a staging texture registered at init time. The BGRA path
// (NvencEncodeBgraTexture) avoids this by caching registration per texture pointer.
```

## Todo

- [ ] Add CPU->GPU fallback comment to AmfEncodeNV12Bytes
- [ ] Add staging copy explanation to NvencEncodeTexture

## Success Criteria

- Comments are accurate and explain the "why"
- No behavioral changes
- Code compiles cleanly

## Risk Assessment

**Low risk**. Comment-only changes. Zero chance of regression.
