# Phase 2: QSV BGRA Support

## Context

AMF and NVENC both support BGRA texture input (eliminating GPU BGRA->NV12 color conversion). QSV currently lacks this:
- `QsvNativeWrapper.cs` line 116: `SupportsBgraInput => false`
- `QsvWrapper.cpp`: no BGRA create/encode functions
- `QsvWrapper.h`: no BGRA API declarations
- `SIPSorceryStreamer.Lifecycle.cs` line 110: BGRA path already works generically via `IBgraEncoder` interface

The `IBgraEncoder` interface (Core/Interfaces/IBgraEncoder.cs) has default implementations returning false, so QSV already "works" without BGRA -- it just falls through to NV12 mode.

## Overview

Add BGRA texture encoding to the QSV wrapper using Media Foundation's `MFVideoFormat_ARGB32` input type. Intel QSV MFTs generally accept ARGB32 as input and perform internal color conversion.

## Key Insights

1. Media Foundation uses `MFVideoFormat_ARGB32` for BGRA pixel format (despite the name, it's actually BGRA byte order)
2. The staging texture format changes from `DXGI_FORMAT_NV12` to `DXGI_FORMAT_B8G8R8A8_UNORM`
3. `FindQsvEncoder` currently hardcodes `MFVideoFormat_NV12` as input type -- needs parameterization
4. Intel QSV HW MFT may or may not accept ARGB32 input -- needs graceful fallback if `SetInputType` fails
5. The C# side only needs: P/Invoke for `QsvCreateEncoderBgra`/`QsvCreateEncoderBgraEx`/`QsvEncodeBgraTexture`, property overrides, and `InitializeBgra`/`EncodeBgraTexture` methods

## Requirements

- Add `QsvCreateEncoderBgra`, `QsvCreateEncoderBgraEx`, `QsvEncodeBgraTexture` to native C API
- Add staging texture with BGRA format
- Update `QsvNativeWrapper.cs` with P/Invoke + wrapper methods
- `SupportsBgraInput` returns true only if Intel MFT accepts ARGB32 (detect at runtime)
- Graceful fallback: if BGRA init fails, caller already handles fallback to NV12 (Lifecycle.cs line 120)

## Architecture

### Native Side (QsvWrapper.cpp)

```
QsvCreateEncoderInternal (existing) -- useBgra param added
  |
  +-- FindQsvEncoder: parameterize input subtype (NV12 or ARGB32)
  +-- SetInputType: MFVideoFormat_NV12 or MFVideoFormat_ARGB32
  +-- Staging texture: DXGI_FORMAT_NV12 or DXGI_FORMAT_B8G8R8A8_UNORM
  +-- Context stores useBgraInput flag

QsvEncodeBgraTexture (new) -- validates BGRA mode, delegates to QsvEncodeTextureInternal
QsvEncodeTexture (existing) -- refactored to call QsvEncodeTextureInternal
```

### C# Side (QsvNativeWrapper.cs)

```
SupportsBgraInput => true (QSV tries BGRA, fallback handled by caller)
InitializeBgra() -- calls QsvCreateEncoderBgraEx or QsvCreateEncoderBgra
EncodeBgraTexture() -- calls QsvEncodeBgraTexture
```

## Related Code Files

| File | Action |
|------|--------|
| `Native/QsvWrapper.h` | MODIFY: add BGRA API declarations |
| `Native/QsvWrapper.cpp` | MODIFY: add BGRA create/encode, refactor internal |
| `Infrastructure/Encoding/QsvNativeWrapper.cs` | MODIFY: add P/Invoke, properties, methods |
| `Core/Interfaces/IBgraEncoder.cs` | NO CHANGE (default implementations handle non-support) |
| `Application/Streaming/SIPSorceryStreamer.Lifecycle.cs` | NO CHANGE (already generic via IBgraEncoder) |

## Implementation Steps

### Step 1: Update QsvWrapper.h
Add after line 113 (before `#ifdef __cplusplus`):
```cpp
// BGRA Input APIs
QSVWRAPPER_API int QsvCreateEncoderBgra(QsvEncoderHandle* outHandle, ID3D11Device* d3d11Device, int width, int height, int fps, int bitrate);
QSVWRAPPER_API int QsvCreateEncoderBgraEx(QsvEncoderHandle* outHandle, ID3D11Device* d3d11Device, int width, int height, int fps, int bitrate, int useHevc);
QSVWRAPPER_API int QsvEncodeBgraTexture(QsvEncoderHandle handle, ID3D11Texture2D* bgraTexture, int forceKeyframe);
```

### Step 2: Update QsvWrapper.cpp - Context struct
Add `bool useBgraInput = false;` to `QsvEncoderContext` (after `bool useHevc` line 111).

### Step 3: Update FindQsvEncoder
Add `GUID inputSubtype` parameter. Change hardcoded `MFVideoFormat_NV12` to parameterized value. Call sites pass `MFVideoFormat_NV12` or `MFVideoFormat_ARGB32`.

### Step 4: Update QsvCreateEncoderInternal
Add `bool useBgra` parameter. When useBgra:
- `FindQsvEncoder` receives `MFVideoFormat_ARGB32`
- Input type uses `MFVideoFormat_ARGB32` instead of `MFVideoFormat_NV12`
- Staging texture format = `DXGI_FORMAT_B8G8R8A8_UNORM`
- Store `ctx->useBgraInput = useBgra`

### Step 5: Add QsvCreateEncoderBgra / QsvCreateEncoderBgraEx
```cpp
QSVWRAPPER_API int QsvCreateEncoderBgra(...) {
    return QsvCreateEncoderInternal(..., true, false);
}
QSVWRAPPER_API int QsvCreateEncoderBgraEx(..., int useHevc) {
    return QsvCreateEncoderInternal(..., true, useHevc != 0);
}
```

### Step 6: Add QsvEncodeBgraTexture
Similar to `QsvEncodeTexture` but validates `ctx->useBgraInput`. Can share encode logic via internal helper or just copy with BGRA guard (prefer refactoring QsvEncodeTexture into internal + thin wrappers).

### Step 7: Update QsvNativeWrapper.cs
- Add P/Invoke: `QsvCreateEncoderBgra`, `QsvCreateEncoderBgraEx`, `QsvEncodeBgraTexture`
- Add `_useBgraMode` field
- Override `SupportsBgraInput => true`
- Override `UsingBgraMode => _useBgraMode`
- Add `InitializeBgra()` method (mirrors AmfNativeWrapper pattern)
- Add `EncodeBgraTexture()` method

### Step 8: Rebuild QsvWrapper.dll and test

## Todo

- [ ] Add `useBgraInput` to QsvEncoderContext struct
- [ ] Parameterize `FindQsvEncoder` input subtype
- [ ] Add `useBgra` param to `QsvCreateEncoderInternal`
- [ ] Implement `QsvCreateEncoderBgra` + `QsvCreateEncoderBgraEx` exports
- [ ] Implement `QsvEncodeBgraTexture` export
- [ ] Update `QsvWrapper.h` with new declarations
- [ ] Add P/Invoke declarations to `QsvNativeWrapper.cs`
- [ ] Add `InitializeBgra`, `EncodeBgraTexture` methods to `QsvNativeWrapper.cs`
- [ ] Update `SupportsBgraInput` property to return `true`
- [ ] Build and smoke test on Intel GPU machine

## Success Criteria

- QsvWrapper.dll builds with new BGRA exports
- On Intel GPU: `InitializeBgra` succeeds and encodes BGRA textures
- If Intel MFT rejects ARGB32: `InitializeBgra` returns false, caller falls back to NV12 (existing behavior)
- `SIPSorceryStreamer.Lifecycle.cs` picks up BGRA mode automatically via `SupportsBgraInput`

## Risk Assessment

**Medium risk**. Intel QSV MFT ARGB32 support varies by driver version and hardware generation. Mitigation: `SetInputType` failure is caught early and returns false, triggering automatic NV12 fallback at the C# layer (Lifecycle.cs line 120). No regression possible because fallback path already works.

## Unresolved Questions

1. Does Intel QSV HW MFT on 11th+ gen CPUs accept `MFVideoFormat_ARGB32`? Need to test on actual hardware. If not, `SupportsBgraInput` could be changed to detect at init time rather than hardcoding `true`.
