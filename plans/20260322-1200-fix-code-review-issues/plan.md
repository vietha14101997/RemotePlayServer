# Fix Code Review Issues - Implementation Plan

**Created**: 2026-03-22
**Status**: Reviewed — 2 high-priority fixes outstanding (see reports/code-reviewer-260322-fix-code-review-issues.md)
**Project**: RemotePlayServer

## Summary

6 issues from code review across native encoder wrappers (AMF/NVENC/QSV) and C# interop layer.
Fixes target DRY violations, missing QSV BGRA support, misleading comments, GOP inconsistency, and GC pressure.

## Phases

| # | Phase | Risk | Status |
|---|-------|------|--------|
| 1 | DRY keyframe detection - extract NalUtils.h | Low | [done] |
| 2 | QSV BGRA support - add BGRA encode path | Medium | [done] |
| 3 | Clarify comments - AmfEncodeNV12Bytes + NvencEncodeTexture | Low | [done] |
| 4 | GOP consistency - fix AmfSetFps overriding infinite GOP | Medium | [done] |
| 5 | GC pressure - ArrayPool in C# callbacks | Medium | [done — 2 follow-ups needed] |

**Note**: Original request mentioned 6 issues. Phase 5 (ArrayPool) applies to 3 wrappers, total 5 logical phases covering all 6 issues.

## File Inventory

### Native C++ (3 wrappers)
- `Native/AmfWrapper/AmfWrapper.cpp` - lines 25-49: duplicated keyframe detect; line 575: GOP override in SetFps
- `Native/NvencWrapper/NvencWrapper.cpp` - lines 24-47: duplicated keyframe detect
- `Native/QsvWrapper/QsvWrapper.cpp` - lines 54-78: duplicated keyframe detect; missing BGRA path
- `Native/QsvWrapper/QsvWrapper.h` - missing BGRA API declarations
- `Native/NalUtils.h` - NEW: shared keyframe detection header

### C# Managed
- `Infrastructure/Encoding/AmfNativeWrapper.cs` - line 472: `new byte[size]` in callback
- `Infrastructure/Encoding/NvencNativeWrapper.cs` - line 441: `new byte[size]` in callback
- `Infrastructure/Encoding/QsvNativeWrapper.cs` - line 328: `new byte[size]` in callback; missing BGRA P/Invoke
- `Core/Interfaces/IBgraEncoder.cs` - already has default implementations, no changes needed

### Build
- `Native/AmfWrapper/CMakeLists.txt` - may need include path update for NalUtils.h

## Dependencies

Phase 1 is independent. Phase 2 depends on nothing. Phases 3-4 are independent. Phase 5 requires analysis of OnEncodedData consumer lifetime (confirmed synchronous within callback scope).

## Execution Order

Recommended: 1 -> 3 -> 4 -> 5 -> 2 (2 is largest, do last)
