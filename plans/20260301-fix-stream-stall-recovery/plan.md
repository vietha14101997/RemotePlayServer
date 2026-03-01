# Fix Stream Stall/Freeze Recovery

**Status:** DONE (2026-03-01)
**Tests:** 105/105 passed | **Build:** 0 errors, 0 warnings

## Problem
Stream freezes on client with no server-side recovery. Three root causes identified:
1. `ProcessFpsFeedback()` logs data but never triggers bitrate adjustment
2. 15s warmup period blocks ALL bitrate reductions (including emergencies)
3. No server-side stall detection -- server only reacts to client messages

## Phases

| Phase | File | Bug | Risk | Status |
|-------|------|-----|------|--------|
| 01 | `SIPSorceryStreamer.Bitrate.cs` | FPS feedback is dead code for bitrate | Low | DONE |
| 02 | `AdaptiveBitrateController.cs` | Warmup blocks emergency reductions | Low | DONE |
| 03 | `PhaseProtocolHandler.Phase3.cs` + `.cs` | No proactive stall detection | Medium | DONE |

## Constraints
- .NET 9.0, C#
- Minimal changes only -- no refactors
- Must not break multi-monitor support
- Must not break existing `QualityFeedback` path
- Use existing `Logger.Info()` pattern
- No new client protocol messages

## Execution Order
Phase 01 -> Phase 02 -> Phase 03 (each is independently deployable)

## Files Modified
- `Application/Streaming/SIPSorceryStreamer.Bitrate.cs` (Phase 01)
- `Application/Streaming/AdaptiveBitrateController.cs` (Phase 02)
- `Application/Protocol/PhaseProtocolHandler.Phase3.cs` (Phase 03)

## Testing
- Unit: extend `AdaptiveBitrateControllerTests.cs` for warmup bypass
- Manual: simulate WiFi throttle, verify bitrate drops within 3s instead of 15s+
- Log verification: search for `[FpsBitrateAction]`, `[EmergencyWarmup]`, `[StallDetect]`

## Success Criteria
- FPS ratio < 50% target with pipeline loss > 30% triggers bitrate reduction
- Emergency reduction fires during warmup when FPS < 30% OR loss > 50%
- Server detects 3s feedback gap and proactively reduces bitrate + keyframe burst
