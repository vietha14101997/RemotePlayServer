# Phase 1: DRY Keyframe Detection - Extract NalUtils.h

## Context

`DetectKeyframeH264` and `DetectKeyframeHEVC` are copy-pasted identically across 3 native wrappers:
- `AmfWrapper.cpp` lines 25-49
- `NvencWrapper.cpp` lines 24-47
- `QsvWrapper.cpp` lines 54-78

All 3 implementations are byte-identical. Any future NAL type change (e.g., adding AV1 OBU detection) must be updated in 3 places.

## Overview

Create a single shared header `Native/NalUtils.h` with `static inline` functions. Each wrapper `#include`s this header and removes its local copy.

## Key Insights

- Functions are pure (no state, no global deps) -> perfect for header-only
- `static inline` avoids ODR violations across translation units
- Header location at `Native/NalUtils.h` is one level above each wrapper folder; include via `#include "../NalUtils.h"`
- No CMakeLists changes needed (header-only, included via relative path)
- MSVC `.vcxproj` builds: header automatically found via relative include

## Requirements

- Identical behavior to current implementation
- No new build dependencies
- All 3 wrappers must compile and link cleanly

## Architecture

```
Native/
  NalUtils.h              <-- NEW: shared header
  AmfWrapper/
    AmfWrapper.cpp         <-- #include "../NalUtils.h", remove local functions
  NvencWrapper/
    NvencWrapper.cpp       <-- #include "../NalUtils.h", remove local functions
  QsvWrapper/
    QsvWrapper.cpp         <-- #include "../NalUtils.h", remove local functions
```

## Related Code Files

| File | Action |
|------|--------|
| `Native/NalUtils.h` | CREATE |
| `Native/AmfWrapper/AmfWrapper.cpp` | MODIFY: remove lines 24-49, add include |
| `Native/NvencWrapper/NvencWrapper.cpp` | MODIFY: remove lines 23-47, add include |
| `Native/QsvWrapper/QsvWrapper.cpp` | MODIFY: remove lines 53-78, add include |

## Implementation Steps

### Step 1: Create NalUtils.h
```cpp
#pragma once
#include <stdint.h>
#include <stddef.h>

// Detect keyframe for H.264: SPS (NAL type 7) or IDR (NAL type 5)
static inline int DetectKeyframeH264(const uint8_t* data, size_t size) { ... }

// Detect keyframe for H.265: VPS (32), SPS (33), IDR_W_RADL (19), IDR_N_LP (20), CRA (21)
static inline int DetectKeyframeHEVC(const uint8_t* data, size_t size) { ... }
```

### Step 2: Update AmfWrapper.cpp
- Add `#include "../NalUtils.h"` after `#include <atomic>` (line 9)
- Delete lines 24-49 (both DetectKeyframe functions + their comments)

### Step 3: Update NvencWrapper.cpp
- Add `#include "../NalUtils.h"` after `#include <vector>` (line 9)
- Delete lines 23-47

### Step 4: Update QsvWrapper.cpp
- Add `#include "../NalUtils.h"` after SafeRelease template (line 51)
- Delete lines 53-78

### Step 5: Verify builds
- Build all 3 DLLs via `build_all.bat` or individual vcxproj

## Todo

- [ ] Create `Native/NalUtils.h` with `static inline` DetectKeyframeH264 + DetectKeyframeHEVC
- [ ] Remove local DetectKeyframeH264/HEVC from AmfWrapper.cpp, add include
- [ ] Remove local DetectKeyframeH264/HEVC from NvencWrapper.cpp, add include
- [ ] Remove local DetectKeyframeH264/HEVC from QsvWrapper.cpp, add include
- [ ] Verify all 3 DLLs build successfully

## Success Criteria

- All 3 wrappers compile without errors
- Keyframe detection behavior unchanged (same NAL types detected)
- Only 1 copy of each function exists in codebase

## Risk Assessment

**Low risk**. Pure refactor with no behavioral change. `static inline` is well-understood in C/C++. If include path fails, compile error is immediate and obvious.
